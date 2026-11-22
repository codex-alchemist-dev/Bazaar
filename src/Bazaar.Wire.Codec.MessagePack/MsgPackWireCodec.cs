using System.Buffers;
using Bazaar.Contracts.Wire;
using Bazaar.Contracts.Wire.Codec;
using Bazaar.Wire.Codec.MessagePack.Generated;
using global::MessagePack;

namespace Bazaar.Wire.Codec.MessagePack;

/// <summary>
/// MessagePack frames, codec name <c>msgpack</c>, binary on the wire (doc 90 section 2). The envelope is a map with the doc 02
/// field names; the payload <c>p</c> is the payload's own MessagePack item, kept as opaque bytes until
/// <see cref="DecodePayload{T}"/>. Payload maps use the JSON camelCase names and omit null fields, so a foreign client can read
/// either shape with the same field names. Readers and writers are generated from the <c>[WirePayload]</c> records over the
/// low-level MessagePack-CSharp reader and writer: no resolver, no typeless mode, no reflection, and nothing is chosen from type
/// names in the data.
/// </summary>
public sealed class MsgPackWireCodec : IWireCodec
{
    /// <summary>The negotiated codec name.</summary>
    public const string CodecName = "msgpack";

    private readonly MsgPackLimits _limits;

    /// <summary>Creates a codec with the default decode limits.</summary>
    public MsgPackWireCodec() : this(WireDecodeLimits.Default)
    {
    }

    /// <summary>Creates a codec with explicit decode limits.</summary>
    public MsgPackWireCodec(WireDecodeLimits limits)
    {
        ArgumentNullException.ThrowIfNull(limits);
        _limits = new MsgPackLimits(limits);
    }

    /// <inheritdoc />
    public string Name => CodecName;

    /// <inheritdoc />
    public bool IsBinary => true;

    /// <summary>The wire payload types this codec can encode and decode, with the message types each travels under.</summary>
    public static IReadOnlyList<(Type Type, IReadOnlyList<string> MessageTypes)> RegisteredPayloads => WireMsgPackPayloads.All;

    /// <inheritdoc />
    public void Encode(in Envelope envelope, IBufferWriter<byte> writer)
    {
        var w = new MessagePackWriter(writer);
        var entries = 3
            + (envelope.Id is null ? 0 : 1)
            + (envelope.Re is null ? 0 : 1)
            + (envelope.S is null ? 0 : 1)
            + (envelope.N is null ? 0 : 1)
            + (envelope.Ts is null ? 0 : 1)
            + (envelope.P.IsEmpty ? 0 : 1);
        w.WriteMapHeader(entries);
        w.WriteString("v"u8);
        w.Write(envelope.V);
        w.WriteString("t"u8);
        w.Write(envelope.T);
        w.WriteString("k"u8);
        w.Write(EnvelopeKinds.ToWire(envelope.K));
        if (envelope.Id is not null) { w.WriteString("id"u8); w.Write(envelope.Id); }
        if (envelope.Re is not null) { w.WriteString("re"u8); w.Write(envelope.Re); }
        if (envelope.S is not null) { w.WriteString("s"u8); w.Write(envelope.S); }
        if (envelope.N is { } n) { w.WriteString("n"u8); w.Write(n); }
        if (envelope.Ts is { } ts) { w.WriteString("ts"u8); w.Write(ts.UtcDateTime); }
        if (!envelope.P.IsEmpty)
        {
            w.WriteString("p"u8);
            w.WriteRaw(envelope.P.Span);
        }

        w.Flush();
    }

    /// <inheritdoc />
    public Envelope Decode(ReadOnlySpan<byte> frame)
    {
        if (frame.IsEmpty) throw new WireFormatException("Frame is empty.");
        try
        {
            return DecodeCore(frame.ToArray());
        }
        catch (Exception ex) when (IsDecodeFailure(ex))
        {
            throw new WireFormatException("Frame is not a valid MessagePack envelope.", ex);
        }
    }

    /// <inheritdoc />
    public ReadOnlyMemory<byte> EncodePayload<T>(T payload)
    {
        if (!WireMsgPackPayloads.IsRegistered<T>()) throw new NotSupportedException($"{typeof(T).Name} is not a registered wire payload.");
        var buffer = new ArrayBufferWriter<byte>();
        var w = new MessagePackWriter(buffer);
        WireMsgPackPayloads.Write(ref w, payload);
        w.Flush();
        return buffer.WrittenMemory;
    }

    /// <inheritdoc />
    public T DecodePayload<T>(in Envelope envelope)
    {
        if (!WireMsgPackPayloads.IsRegistered<T>()) throw new NotSupportedException($"{typeof(T).Name} is not a registered wire payload.");
        if (envelope.P.IsEmpty) throw new WireFormatException("Message has no payload.");
        try
        {
            var reader = new MessagePackReader(envelope.P);
            var payload = WireMsgPackPayloads.Read<T>(ref reader, _limits);
            if (!reader.End) throw new WireFormatException("Data follows the MessagePack item.");
            return payload;
        }
        catch (Exception ex) when (IsDecodeFailure(ex))
        {
            throw new WireFormatException("Payload does not match the message type.", ex);
        }
    }

    private Envelope DecodeCore(byte[] frame)
    {
        var r = new MessagePackReader(frame);
        var count = MsgPackWireRuntime.ReadMapStart(ref r, _limits);

        int? v = null;
        string? t = null;
        string? k = null;
        string? id = null, re = null, s = null;
        long? n = null;
        DateTimeOffset? ts = null;
        ReadOnlyMemory<byte> p = default;
        var seen = 0;

        for (var i = 0; i < count; i++)
        {
            var key = MsgPackWireRuntime.ReadKey(ref r, _limits);
            if (key.SequenceEqual("v"u8)) { Once(ref seen, 1, "v"); v = r.ReadInt32(); }
            else if (key.SequenceEqual("t"u8)) { Once(ref seen, 2, "t"); t = MsgPackWireRuntime.ReadText(ref r, _limits); }
            else if (key.SequenceEqual("k"u8)) { Once(ref seen, 4, "k"); k = MsgPackWireRuntime.ReadText(ref r, _limits); }
            else if (key.SequenceEqual("id"u8)) { Once(ref seen, 8, "id"); id = MsgPackWireRuntime.ReadOptionalText(ref r, _limits); }
            else if (key.SequenceEqual("re"u8)) { Once(ref seen, 16, "re"); re = MsgPackWireRuntime.ReadOptionalText(ref r, _limits); }
            else if (key.SequenceEqual("s"u8)) { Once(ref seen, 32, "s"); s = MsgPackWireRuntime.ReadOptionalText(ref r, _limits); }
            else if (key.SequenceEqual("n"u8))
            {
                Once(ref seen, 64, "n");
                if (!r.TryReadNil()) n = r.ReadInt64();
            }
            else if (key.SequenceEqual("ts"u8))
            {
                Once(ref seen, 128, "ts");
                if (!r.TryReadNil()) ts = new DateTimeOffset(r.ReadDateTime());
            }
            else if (key.SequenceEqual("p"u8))
            {
                Once(ref seen, 256, "p");
                // Validate and measure the payload without decoding it, then keep its bytes.
                var start = (int)r.Consumed;
                MsgPackWireRuntime.SkipValue(ref r, _limits);
                p = frame.AsMemory(start, (int)r.Consumed - start);
            }
            else
            {
                // Unknown envelope field: a later minor version added it. Ignore.
                MsgPackWireRuntime.SkipValue(ref r, _limits);
            }
        }

        MsgPackWireRuntime.Leave(ref r);
        if (!r.End) throw new WireFormatException("Data follows the MessagePack item.");
        if (v is null) throw new WireFormatException("Envelope has no 'v'.");
        if (string.IsNullOrEmpty(t)) throw new WireFormatException("Envelope has no 't'.");
        if (!EnvelopeKinds.TryParse(k, out var kind)) throw new WireFormatException("Envelope 'k' is missing or unknown.");

        return new Envelope(v.Value, t, kind, id, re, s, n, ts, p);
    }

    private static void Once(ref int seen, int bit, string key)
    {
        if ((seen & bit) != 0) throw MsgPackWireRuntime.DuplicateKey(key);
        seen |= bit;
    }

    private static bool IsDecodeFailure(Exception ex) =>
        ex is MessagePackSerializationException
            or EndOfStreamException
            or InvalidOperationException
            or OverflowException
            or ArgumentException
            or FormatException
            or InvalidCastException
            or InsufficientMemoryException
            or WireFormatException;
}
