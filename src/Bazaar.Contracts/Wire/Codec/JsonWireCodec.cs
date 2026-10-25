using System.Buffers;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Bazaar.Contracts.Wire.Codec;

/// <summary>
/// JSON text frames. Envelope fields are written by hand so the payload stays raw JSON; payloads use
/// the source-generated <see cref="WireJsonContext"/>, so the codec is trim and AOT safe.
/// </summary>
public sealed class JsonWireCodec : IWireCodec
{
    /// <summary>The negotiated codec name.</summary>
    public const string CodecName = "json";

    /// <inheritdoc />
    public string Name => CodecName;

    /// <inheritdoc />
    public bool IsBinary => false;

    /// <inheritdoc />
    public void Encode(in Envelope envelope, IBufferWriter<byte> writer)
    {
        using var json = new Utf8JsonWriter(writer);
        json.WriteStartObject();
        json.WriteNumber("v"u8, envelope.V);
        json.WriteString("t"u8, envelope.T);
        json.WriteString("k"u8, EnvelopeKinds.ToWire(envelope.K));
        if (envelope.Id is not null) json.WriteString("id"u8, envelope.Id);
        if (envelope.Re is not null) json.WriteString("re"u8, envelope.Re);
        if (envelope.S is not null) json.WriteString("s"u8, envelope.S);
        if (envelope.N is { } n) json.WriteNumber("n"u8, n);
        if (envelope.Ts is { } ts) json.WriteString("ts"u8, ts.ToUniversalTime());
        if (!envelope.P.IsEmpty)
        {
            json.WritePropertyName("p"u8);
            json.WriteRawValue(envelope.P.Span, skipInputValidation: false);
        }

        json.WriteEndObject();
    }

    /// <inheritdoc />
    public Envelope Decode(ReadOnlySpan<byte> frame)
    {
        try
        {
            return DecodeCore(frame);
        }
        catch (JsonException ex)
        {
            throw new WireFormatException("Frame is not valid JSON.", ex);
        }
        catch (InvalidOperationException ex)
        {
            throw new WireFormatException("A frame field has the wrong type.", ex);
        }
        catch (FormatException ex) when (ex is not WireFormatException)
        {
            throw new WireFormatException("A frame field is malformed.", ex);
        }
    }

    /// <inheritdoc />
    public ReadOnlyMemory<byte> EncodePayload<T>(T payload)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var json = new Utf8JsonWriter(buffer))
        {
            JsonSerializer.Serialize(json, payload, Info<T>());
        }

        return buffer.WrittenMemory;
    }

    /// <inheritdoc />
    public T DecodePayload<T>(in Envelope envelope)
    {
        if (envelope.P.IsEmpty) throw new WireFormatException("Message has no payload.");
        try
        {
            return JsonSerializer.Deserialize(envelope.P.Span, Info<T>())
                ?? throw new WireFormatException("Payload is null.");
        }
        catch (JsonException ex)
        {
            throw new WireFormatException("Payload does not match the message type.", ex);
        }
    }

    private static Envelope DecodeCore(ReadOnlySpan<byte> frame)
    {
        var reader = new Utf8JsonReader(frame);
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
        {
            throw new WireFormatException("Frame is not a JSON object.");
        }

        int? v = null;
        string? t = null;
        string? k = null;
        string? id = null, re = null, s = null;
        long? n = null;
        DateTimeOffset? ts = null;
        ReadOnlyMemory<byte> p = default;

        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            if (reader.ValueTextEquals("v"u8)) { reader.Read(); v = reader.GetInt32(); }
            else if (reader.ValueTextEquals("t"u8)) { reader.Read(); t = reader.GetString(); }
            else if (reader.ValueTextEquals("k"u8)) { reader.Read(); k = reader.GetString(); }
            else if (reader.ValueTextEquals("id"u8)) { reader.Read(); id = ReadOptionalString(ref reader); }
            else if (reader.ValueTextEquals("re"u8)) { reader.Read(); re = ReadOptionalString(ref reader); }
            else if (reader.ValueTextEquals("s"u8)) { reader.Read(); s = ReadOptionalString(ref reader); }
            else if (reader.ValueTextEquals("n"u8)) { reader.Read(); n = reader.TokenType == JsonTokenType.Null ? null : reader.GetInt64(); }
            else if (reader.ValueTextEquals("ts"u8)) { reader.Read(); ts = reader.TokenType == JsonTokenType.Null ? null : reader.GetDateTimeOffset(); }
            else if (reader.ValueTextEquals("p"u8))
            {
                reader.Read();
                long start = reader.TokenStartIndex;
                reader.Skip();
                p = frame.Slice((int)start, (int)(reader.BytesConsumed - start)).ToArray();
            }
            else
            {
                // Unknown envelope field: a later minor version added it. Ignore.
                reader.Read();
                reader.Skip();
            }
        }

        if (reader.TokenType != JsonTokenType.EndObject) throw new WireFormatException("Frame object is not closed.");
        if (v is null) throw new WireFormatException("Envelope has no 'v'.");
        if (string.IsNullOrEmpty(t)) throw new WireFormatException("Envelope has no 't'.");
        if (!EnvelopeKinds.TryParse(k, out var kind)) throw new WireFormatException("Envelope 'k' is missing or unknown.");

        return new Envelope(v.Value, t, kind, id, re, s, n, ts, p);
    }

    private static string? ReadOptionalString(ref Utf8JsonReader reader) =>
        reader.TokenType == JsonTokenType.Null ? null : reader.GetString();

    private static JsonTypeInfo<T> Info<T>() =>
        WireJsonContext.Default.GetTypeInfo(typeof(T)) as JsonTypeInfo<T>
        ?? throw new NotSupportedException($"{typeof(T).Name} is not a registered wire payload.");
}
