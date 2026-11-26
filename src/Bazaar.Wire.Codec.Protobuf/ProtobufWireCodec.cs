using System.Buffers;
using Bazaar.Contracts.Wire;
using Bazaar.Contracts.Wire.Codec;
using Bazaar.Wire.Codec.Protobuf.Generated;
using Google.Protobuf;

namespace Bazaar.Wire.Codec.Protobuf;

/// <summary>
/// Protobuf frames, codec name <c>protobuf</c>, binary on the wire (doc 90 section 2). The envelope is the <c>Envelope</c>
/// message of the generated <c>wire.proto</c>; the payload <c>p</c> is the payload's own message, kept as opaque bytes until
/// <see cref="DecodePayload{T}"/>. The mirror messages come from protoc, the mapping between them and the contract records is
/// generated, so nothing is reflected and nothing is chosen from type names in the data. The decoder is stricter than plain
/// Protobuf for the envelope: known fields must come in ascending order, once, and the closing <c>end</c> field must be there,
/// so a truncated frame or two frames glued together is rejected instead of merged.
/// </summary>
public sealed class ProtobufWireCodec : IWireCodec
{
    /// <summary>The negotiated codec name.</summary>
    public const string CodecName = "protobuf";

    private const int TagV = (1 << 3) | 0;
    private const int TagT = (2 << 3) | 2;
    private const int TagK = (3 << 3) | 2;
    private const int TagId = (4 << 3) | 2;
    private const int TagRe = (5 << 3) | 2;
    private const int TagS = (6 << 3) | 2;
    private const int TagN = (7 << 3) | 0;
    private const int TagTs = (8 << 3) | 2;
    private const int TagP = (9 << 3) | 2;
    private const int TagEnd = (10 << 3) | 0;

    private readonly ProtobufLimits _limits;

    /// <summary>Creates a codec with the default decode limits.</summary>
    public ProtobufWireCodec() : this(WireDecodeLimits.Default)
    {
    }

    /// <summary>Creates a codec with explicit decode limits.</summary>
    public ProtobufWireCodec(WireDecodeLimits limits)
    {
        ArgumentNullException.ThrowIfNull(limits);
        _limits = new ProtobufLimits(limits);
    }

    /// <inheritdoc />
    public string Name => CodecName;

    /// <inheritdoc />
    public bool IsBinary => true;

    /// <summary>The wire payload types this codec can encode and decode, with the message types each travels under.</summary>
    public static IReadOnlyList<(Type Type, IReadOnlyList<string> MessageTypes)> RegisteredPayloads => WireProtobufPayloads.All;

    /// <inheritdoc />
    public void Encode(in Envelope envelope, IBufferWriter<byte> writer)
    {
        var message = new Proto.Envelope
        {
            V = envelope.V,
            T = envelope.T,
            K = EnvelopeKinds.ToWire(envelope.K),
            End = true,
        };
        if (envelope.Id is not null) message.Id = envelope.Id;
        if (envelope.Re is not null) message.Re = envelope.Re;
        if (envelope.S is not null) message.S = envelope.S;
        if (envelope.N is { } n) message.N = n;
        if (envelope.Ts is { } ts) message.Ts = ProtobufWireRuntime.ToInstant(ts);
        if (!envelope.P.IsEmpty) message.P = ByteString.CopyFrom(envelope.P.Span);
        MessageExtensions.WriteTo(message, writer);
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
            throw new WireFormatException("Frame is not a valid Protobuf envelope.", ex);
        }
    }

    /// <inheritdoc />
    public ReadOnlyMemory<byte> EncodePayload<T>(T payload)
    {
        if (!WireProtobufPayloads.IsRegistered<T>()) throw new NotSupportedException($"{typeof(T).Name} is not a registered wire payload.");
        var bytes = WireProtobufPayloads.Write(payload);
        return bytes.Length == 0 ? ProtobufWireRuntime.EmptyPayload() : bytes;
    }

    /// <inheritdoc />
    public T DecodePayload<T>(in Envelope envelope)
    {
        if (!WireProtobufPayloads.IsRegistered<T>()) throw new NotSupportedException($"{typeof(T).Name} is not a registered wire payload.");
        if (envelope.P.IsEmpty) throw new WireFormatException("Message has no payload.");
        try
        {
            return WireProtobufPayloads.Read<T>(envelope.P, _limits);
        }
        catch (Exception ex) when (IsDecodeFailure(ex))
        {
            throw new WireFormatException("Payload does not match the message type.", ex);
        }
    }

    private Envelope DecodeCore(byte[] frame)
    {
        using var stream = new MemoryStream(frame, writable: false);
        var input = CodedInputStream.CreateWithLimits(stream, frame.Length, Math.Max(_limits.MaxDepth, 1));

        int? v = null;
        string? t = null;
        string? k = null;
        string? id = null, re = null, s = null;
        long? n = null;
        DateTimeOffset? ts = null;
        ReadOnlyMemory<byte> p = default;
        var sawEnd = false;
        var lastField = 0;

        uint tag;
        while ((tag = input.ReadTag()) != 0)
        {
            var field = WireFormat.GetTagFieldNumber(tag);
            if (field is >= 1 and <= 10)
            {
                if (field <= lastField) throw new WireFormatException("Envelope fields are repeated or out of order.");
                lastField = field;
            }

            switch (tag)
            {
                case TagV: v = input.ReadInt32(); break;
                case TagT: t = ReadText(input); break;
                case TagK: k = ReadText(input); break;
                case TagId: id = ReadText(input); break;
                case TagRe: re = ReadText(input); break;
                case TagS: s = ReadText(input); break;
                case TagN: n = input.ReadInt64(); break;
                case TagTs: ts = ProtobufWireRuntime.FromInstant(Proto.Instant.Parser.ParseFrom(input.ReadBytes())); break;
                case TagP: p = input.ReadBytes().Memory; break;
                case TagEnd:
                    if (!input.ReadBool()) throw new WireFormatException("The envelope end marker is false.");
                    sawEnd = true;
                    break;
                default:
                    if (field is >= 1 and <= 10) throw new WireFormatException("An envelope field has the wrong wire type.");
                    // Unknown envelope field: a later minor version added it. Ignore (groups nest, so the recursion cap applies).
                    input.SkipLastField();
                    break;
            }
        }

        if (!sawEnd) throw new WireFormatException("The envelope is truncated: no end marker.");
        if (v is null) throw new WireFormatException("Envelope has no 'v'.");
        if (string.IsNullOrEmpty(t)) throw new WireFormatException("Envelope has no 't'.");
        if (!EnvelopeKinds.TryParse(k, out var kind)) throw new WireFormatException("Envelope 'k' is missing or unknown.");

        return new Envelope(v.Value, t, kind, id, re, s, n, ts, p);
    }

    private string ReadText(CodedInputStream input) => ProtobufWireRuntime.Text(input.ReadString(), _limits);

    private static bool IsDecodeFailure(Exception ex) =>
        ex is InvalidProtocolBufferException
            or InvalidOperationException
            or OverflowException
            or ArgumentException
            or FormatException
            or InvalidCastException
            or IOException
            or InsufficientMemoryException
            or WireFormatException;
}
