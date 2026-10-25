using System.Buffers;
using Google.Protobuf;
using P = Spike.S6.Wire.Proto;

namespace Spike.S6.Wire;

/// <summary>
/// Binary frames. The generated classes are mutable and not records, so a mapping layer
/// (below) is needed to keep the shared C# contracts; this is the cost of Protobuf for .NET-only code.
/// </summary>
public sealed class ProtoWireCodec : IWireCodec
{
    public string Name => "protobuf";
    public bool IsBinary => true;

    public void Encode(in Envelope e, IBufferWriter<byte> w)
    {
        var m = new P.Envelope { V = e.V, T = e.T, K = (P.Kind)((int)e.K + 1), TsUnixMs = e.Ts.ToUnixTimeMilliseconds() };
        if (e.Id is not null) m.Id = e.Id;
        if (e.Re is not null) m.Re = e.Re;
        if (e.S is not null) m.S = e.S;
        if (e.N is { } n) m.N = n;
        if (!e.P.IsEmpty) m.P = UnsafeByteOperations.UnsafeWrap(e.P);
        m.WriteTo(w);
    }

    public Envelope Decode(ReadOnlySpan<byte> frame)
    {
        var m = P.Envelope.Parser.ParseFrom(frame);
        return new Envelope(m.V, m.T, (Kind)((int)m.K - 1), m.HasId ? m.Id : null, m.HasRe ? m.Re : null,
            m.HasS ? m.S : null, m.HasN ? m.N : null, DateTimeOffset.FromUnixTimeMilliseconds(m.TsUnixMs),
            m.P.ToByteArray());
    }

    public void EncodePayload<T>(T payload, IBufferWriter<byte> w) => ToMessage(payload!).WriteTo(w);

    public T DecodePayload<T>(in Envelope e)
    {
        var s = e.P.Span;
        IMessage m = typeof(T) switch
        {
            var t when t == typeof(Hello) => P.Hello.Parser.ParseFrom(s),
            var t when t == typeof(Subscribe) => P.Subscribe.Parser.ParseFrom(s),
            var t when t == typeof(GcMessage) => P.GcMessage.Parser.ParseFrom(s),
            var t when t == typeof(GcPage) => P.GcPage.Parser.ParseFrom(s),
            var t when t == typeof(Lagged) => P.Lagged.Parser.ParseFrom(s),
            var t when t == typeof(GcMessageV11) => P.GcMessageV11.Parser.ParseFrom(s),
            _ => throw new NotSupportedException(typeof(T).Name),
        };
        return (T)FromMessage(m);
    }

    internal static IMessage ToMessage(object payload) => payload switch
    {
        Hello h => ToProto(h),
        Subscribe s => new P.Subscribe { Streams = { s.Streams.Select(x => new P.StreamRef { S = x.S, After = x.After, EditAfter = x.EditAfter }) } },
        GcMessage g => ToProto(g),
        GcPage g => new P.GcPage { Items = { g.Items.Select(ToProto) } },
        Lagged l => new P.Lagged { S = l.S, LastSent = l.LastSent },
        GcMessageV11 g => new P.GcMessageV11
        {
            GcId = g.GcId, N = g.N, Ev = g.Ev, MessageId = g.MessageId, AuthorId = g.AuthorId, Text = g.Text,
            TsUnixMs = g.Ts.ToUnixTimeMilliseconds(), Edited = g.Edited, Mentions = { g.Mentions }, ThreadId = g.ThreadId ?? "",
        },
        _ => throw new NotSupportedException(payload.GetType().Name),
    };

    internal static object FromMessage(IMessage m) => m switch
    {
        P.Hello h => FromProto(h),
        P.Subscribe s => new Subscribe([.. s.Streams.Select(x => new StreamRef(x.S, x.After, x.EditAfter))]),
        P.GcMessage g => FromProto(g),
        P.GcPage g => new GcPage([.. g.Items.Select(FromProto)]),
        P.Lagged l => FromProto(l),
        P.GcMessageV11 g => FromProto(g),
        _ => throw new NotSupportedException(m.GetType().Name),
    };

    private static P.Hello ToProto(Hello h) => new()
    {
        VersionMin = h.VersionMin, VersionMax = h.VersionMax, Codecs = { h.Codecs },
        DeviceId = h.DeviceId, DeviceClass = h.DeviceClass, ClientBuild = h.ClientBuild,
        Cursors = { h.Cursors.Select(c => new P.Cursor { S = c.S, After = c.After, EditAfter = c.EditAfter }) },
    };

    private static Hello FromProto(P.Hello h) => new(h.VersionMin, h.VersionMax, [.. h.Codecs],
        h.DeviceId, h.DeviceClass, h.ClientBuild, [.. h.Cursors.Select(c => new Cursor(c.S, c.After, c.EditAfter))]);

    private static P.GcMessage ToProto(GcMessage g) => new()
    {
        GcId = g.GcId, N = g.N, Ev = g.Ev, MessageId = g.MessageId, AuthorId = g.AuthorId, Text = g.Text,
        TsUnixMs = g.Ts.ToUnixTimeMilliseconds(), Edited = g.Edited, Mentions = { g.Mentions },
    };

    private static GcMessage FromProto(P.GcMessage g) => new(g.GcId, g.N, g.Ev, g.MessageId, g.AuthorId, g.Text,
        DateTimeOffset.FromUnixTimeMilliseconds(g.TsUnixMs), g.Edited, [.. g.Mentions]);

    private static GcMessageV11 FromProto(P.GcMessageV11 g) => new(g.GcId, g.N, g.Ev, g.MessageId, g.AuthorId, g.Text,
        DateTimeOffset.FromUnixTimeMilliseconds(g.TsUnixMs), g.Edited, [.. g.Mentions], g.ThreadId.Length == 0 ? null : g.ThreadId);

    private static Lagged FromProto(P.Lagged l) => new(l.S, l.LastSent);
}
