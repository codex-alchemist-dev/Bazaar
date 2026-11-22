using System.Buffers;
using System.Formats.Cbor;
using Bazaar.Contracts.Wire;
using Bazaar.Contracts.Wire.Codec;
using Bazaar.Wire.Codec.Cbor.Generated;

namespace Bazaar.Wire.Codec.Cbor;

/// <summary>
/// CBOR frames (RFC 8949), codec name <c>cbor</c>, binary on the wire (doc 90 section 2). The envelope is a map with the doc 02
/// field names; the payload <c>p</c> is the payload's own CBOR data item, kept as opaque bytes until
/// <see cref="DecodePayload{T}"/>. Payload maps use the JSON camelCase names and omit null fields, so a foreign client can read
/// either shape with the same field names. Readers and writers are generated from the <c>[WirePayload]</c> records, so there is
/// no reflection and nothing is chosen from type names in the data.
/// </summary>
public sealed class CborWireCodec : IWireCodec
{
    /// <summary>The negotiated codec name.</summary>
    public const string CodecName = "cbor";

    private readonly CborLimits _limits;

    /// <summary>Creates a codec with the default decode limits.</summary>
    public CborWireCodec() : this(WireDecodeLimits.Default)
    {
    }

    /// <summary>Creates a codec with explicit decode limits.</summary>
    public CborWireCodec(WireDecodeLimits limits)
    {
        ArgumentNullException.ThrowIfNull(limits);
        _limits = new CborLimits(limits);
    }

    /// <inheritdoc />
    public string Name => CodecName;

    /// <inheritdoc />
    public bool IsBinary => true;

    /// <summary>The wire payload types this codec can encode and decode, with the message types each travels under.</summary>
    public static IReadOnlyList<(Type Type, IReadOnlyList<string> MessageTypes)> RegisteredPayloads => WireCborPayloads.All;

    /// <inheritdoc />
    public void Encode(in Envelope envelope, IBufferWriter<byte> writer)
    {
        var cbor = NewWriter();
        var entries = 3
            + (envelope.Id is null ? 0 : 1)
            + (envelope.Re is null ? 0 : 1)
            + (envelope.S is null ? 0 : 1)
            + (envelope.N is null ? 0 : 1)
            + (envelope.Ts is null ? 0 : 1)
            + (envelope.P.IsEmpty ? 0 : 1);
        cbor.WriteStartMap(entries);
        cbor.WriteTextString("v");
        cbor.WriteInt32(envelope.V);
        cbor.WriteTextString("t");
        cbor.WriteTextString(envelope.T);
        cbor.WriteTextString("k");
        cbor.WriteTextString(EnvelopeKinds.ToWire(envelope.K));
        if (envelope.Id is not null) { cbor.WriteTextString("id"); cbor.WriteTextString(envelope.Id); }
        if (envelope.Re is not null) { cbor.WriteTextString("re"); cbor.WriteTextString(envelope.Re); }
        if (envelope.S is not null) { cbor.WriteTextString("s"); cbor.WriteTextString(envelope.S); }
        if (envelope.N is { } n) { cbor.WriteTextString("n"); cbor.WriteInt64(n); }
        if (envelope.Ts is { } ts) { cbor.WriteTextString("ts"); cbor.WriteDateTimeOffset(ts.ToUniversalTime()); }
        if (!envelope.P.IsEmpty)
        {
            cbor.WriteTextString("p");
            cbor.WriteEncodedValue(envelope.P.Span);
        }

        cbor.WriteEndMap();
        Flush(cbor, writer);
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
            throw new WireFormatException("Frame is not a valid CBOR envelope.", ex);
        }
    }

    /// <inheritdoc />
    public ReadOnlyMemory<byte> EncodePayload<T>(T payload)
    {
        if (!WireCborPayloads.IsRegistered<T>()) throw new NotSupportedException($"{typeof(T).Name} is not a registered wire payload.");
        var cbor = NewWriter();
        WireCborPayloads.Write(cbor, payload);
        return cbor.Encode();
    }

    /// <inheritdoc />
    public T DecodePayload<T>(in Envelope envelope)
    {
        if (!WireCborPayloads.IsRegistered<T>()) throw new NotSupportedException($"{typeof(T).Name} is not a registered wire payload.");
        if (envelope.P.IsEmpty) throw new WireFormatException("Message has no payload.");
        try
        {
            var reader = NewReader(envelope.P);
            var payload = WireCborPayloads.Read<T>(reader, _limits);
            EnsureFinished(reader);
            return payload;
        }
        catch (Exception ex) when (IsDecodeFailure(ex))
        {
            throw new WireFormatException("Payload does not match the message type.", ex);
        }
    }

    private Envelope DecodeCore(byte[] frame)
    {
        var r = NewReader(frame);
        CborWireRuntime.ReadMapStart(r, _limits);

        int? v = null;
        string? t = null;
        string? k = null;
        string? id = null, re = null, s = null;
        long? n = null;
        DateTimeOffset? ts = null;
        ReadOnlyMemory<byte> p = default;

        var items = 0;
        while (CborWireRuntime.NextKey(r, _limits, ref items, out var key))
        {
            switch (key)
            {
                case "v": v = r.ReadInt32(); break;
                case "t": t = CborWireRuntime.ReadText(r, _limits); break;
                case "k": k = CborWireRuntime.ReadText(r, _limits); break;
                case "id": id = ReadOptionalText(r); break;
                case "re": re = ReadOptionalText(r); break;
                case "s": s = ReadOptionalText(r); break;
                case "n":
                    if (r.PeekState() == CborReaderState.Null) r.ReadNull(); else n = r.ReadInt64();
                    break;
                case "ts":
                    if (r.PeekState() == CborReaderState.Null) r.ReadNull(); else ts = r.ReadDateTimeOffset();
                    break;
                case "p":
                    {
                        // Validate and measure the payload without decoding it, then keep its bytes.
                        var before = r.BytesRemaining;
                        CborWireRuntime.SkipValue(r, _limits);
                        var length = before - r.BytesRemaining;
                        p = frame.AsMemory(frame.Length - before, length);
                        break;
                    }

                default:
                    // Unknown envelope field: a later minor version added it. Ignore.
                    CborWireRuntime.SkipValue(r, _limits);
                    break;
            }
        }

        EnsureFinished(r);
        if (v is null) throw new WireFormatException("Envelope has no 'v'.");
        if (string.IsNullOrEmpty(t)) throw new WireFormatException("Envelope has no 't'.");
        if (!EnvelopeKinds.TryParse(k, out var kind)) throw new WireFormatException("Envelope 'k' is missing or unknown.");

        return new Envelope(v.Value, t, kind, id, re, s, n, ts, p);

        string? ReadOptionalText(CborReader reader)
        {
            if (reader.PeekState() != CborReaderState.Null) return CborWireRuntime.ReadText(reader, _limits);
            reader.ReadNull();
            return null;
        }
    }

    private static bool IsDecodeFailure(Exception ex) =>
        ex is CborContentException
            or InvalidOperationException
            or OverflowException
            or ArgumentException
            or FormatException
            or InvalidCastException
            or InsufficientMemoryException
            or WireFormatException;

    private static void EnsureFinished(CborReader reader)
    {
        if (reader.BytesRemaining != 0 || reader.PeekState() != CborReaderState.Finished) throw new WireFormatException("Data follows the CBOR item.");
    }

    private static CborWriter NewWriter() => new(CborConformanceMode.Strict);

    private static CborReader NewReader(ReadOnlyMemory<byte> data) => new(data, CborConformanceMode.Strict);

    private static void Flush(CborWriter cbor, IBufferWriter<byte> writer)
    {
        var length = cbor.BytesWritten;
        var span = writer.GetSpan(length);
        if (!cbor.TryEncode(span, out var written)) throw new InvalidOperationException("The buffer writer returned too small a span.");
        writer.Advance(written);
    }
}
