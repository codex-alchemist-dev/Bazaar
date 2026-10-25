using System.Buffers;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Spike.S6.Wire;

/// <summary>Text frames. Envelope fields are written by hand so the payload stays raw JSON bytes.</summary>
public sealed class JsonWireCodec : IWireCodec
{
    public string Name => "json";
    public bool IsBinary => false;

    public void Encode(in Envelope e, IBufferWriter<byte> w)
    {
        using var u = new Utf8JsonWriter(w);
        u.WriteStartObject();
        u.WriteNumber("v"u8, e.V);
        u.WriteString("t"u8, e.T);
        u.WriteString("k"u8, KindName(e.K));
        if (e.Id is not null) u.WriteString("id"u8, e.Id);
        if (e.Re is not null) u.WriteString("re"u8, e.Re);
        if (e.S is not null) u.WriteString("s"u8, e.S);
        if (e.N is { } n) u.WriteNumber("n"u8, n);
        u.WriteString("ts"u8, e.Ts);
        if (!e.P.IsEmpty)
        {
            u.WritePropertyName("p"u8);
            u.WriteRawValue(e.P.Span, skipInputValidation: true);
        }
        u.WriteEndObject();
    }

    public Envelope Decode(ReadOnlySpan<byte> frame)
    {
        var r = new Utf8JsonReader(frame);
        if (!r.Read() || r.TokenType != JsonTokenType.StartObject) throw new FormatException("not an object");
        int v = 0; string t = "", k = "req"; string? id = null, re = null, s = null; long? n = null;
        DateTimeOffset ts = default; ReadOnlyMemory<byte> p = default;
        while (r.Read() && r.TokenType == JsonTokenType.PropertyName)
        {
            if (r.ValueTextEquals("v"u8)) { r.Read(); v = r.GetInt32(); }
            else if (r.ValueTextEquals("t"u8)) { r.Read(); t = r.GetString()!; }
            else if (r.ValueTextEquals("k"u8)) { r.Read(); k = r.GetString()!; }
            else if (r.ValueTextEquals("id"u8)) { r.Read(); id = r.GetString(); }
            else if (r.ValueTextEquals("re"u8)) { r.Read(); re = r.GetString(); }
            else if (r.ValueTextEquals("s"u8)) { r.Read(); s = r.GetString(); }
            else if (r.ValueTextEquals("n"u8)) { r.Read(); n = r.GetInt64(); }
            else if (r.ValueTextEquals("ts"u8)) { r.Read(); ts = r.GetDateTimeOffset(); }
            else if (r.ValueTextEquals("p"u8))
            {
                r.Read();
                long start = r.TokenStartIndex;
                r.Skip();
                long end = r.BytesConsumed;
                p = frame.Slice((int)start, (int)(end - start)).ToArray();
            }
            else { r.Read(); r.Skip(); } // unknown envelope field: ignored (minor versions add fields)
        }
        return new Envelope(v, t, ParseKind(k), id, re, s, n, ts, p);
    }

    public void EncodePayload<T>(T payload, IBufferWriter<byte> w)
    {
        using var u = new Utf8JsonWriter(w);
        JsonSerializer.Serialize(u, payload, Info<T>());
    }

    public T DecodePayload<T>(in Envelope e) =>
        JsonSerializer.Deserialize(e.P.Span, Info<T>()) ?? throw new FormatException("null payload");

    private static JsonTypeInfo<T> Info<T>() =>
        (JsonTypeInfo<T>?)WireJsonContext.Default.GetTypeInfo(typeof(T)) ?? throw new NotSupportedException(typeof(T).Name);

    private static string KindName(Kind k) => k switch
    {
        Kind.Req => "req", Kind.Res => "res", Kind.Evt => "evt", _ => "err",
    };

    private static Kind ParseKind(string k) => k switch
    {
        "req" => Kind.Req, "res" => Kind.Res, "evt" => Kind.Evt, "err" => Kind.Err,
        _ => throw new FormatException("unknown kind " + k),
    };
}
