using System.Buffers;
using Bazaar.Contracts.Wire;
using Bazaar.Contracts.Wire.Codec;
using Bazaar.Wire.Codec.MemoryPack.Generated;
using global::MemoryPack;
using global::MemoryPack.Internal;

namespace Bazaar.Wire.Codec.MemoryPack;

/// <summary>
/// MemoryPack frames, codec name <c>memorypack</c>, binary on the wire, .NET to .NET only (doc 90 sections 2 and 3.2). The
/// envelope and every payload are MemoryPack version-tolerant objects: a member count, a varint length per member, then the member
/// bytes. The payload <c>p</c> is one member holding the payload object's bytes, kept opaque until <see cref="DecodePayload{T}"/>.
/// Readers and writers are generated from the <c>[WirePayload]</c> records and use only MemoryPack's public reader and writer, so
/// nothing is chosen from type names in the data and every decode limit is checked while reading.
/// </summary>
public sealed class MemoryPackWireCodec : IWireCodec
{
    /// <summary>The negotiated codec name.</summary>
    public const string CodecName = "memorypack";

    // Envelope member numbers, 0-based, append-only: v t k id re s n ts p.
    internal const int EnvelopeMembers = 9;

    private readonly MemoryPackLimits _limits;

    /// <summary>Creates a codec with the default decode limits.</summary>
    public MemoryPackWireCodec() : this(WireDecodeLimits.Default)
    {
    }

    /// <summary>Creates a codec with explicit decode limits.</summary>
    public MemoryPackWireCodec(WireDecodeLimits limits)
    {
        ArgumentNullException.ThrowIfNull(limits);
        // MemoryPack writes numbers and float arrays as raw memory, so both ends must agree on byte order.
        if (!BitConverter.IsLittleEndian) throw new PlatformNotSupportedException("The memorypack codec needs a little-endian platform.");
        _limits = new MemoryPackLimits(limits);
    }

    /// <inheritdoc />
    public string Name => CodecName;

    /// <inheritdoc />
    public bool IsBinary => true;

    /// <summary>The wire payload types this codec can encode and decode, with the message types each travels under.</summary>
    public static IReadOnlyList<(Type Type, IReadOnlyList<string> MessageTypes)> RegisteredPayloads => WireMemoryPackPayloads.All;

    /// <inheritdoc />
    public void Encode(in Envelope envelope, IBufferWriter<byte> writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        using var state = MemoryPackWriterOptionalStatePool.Rent(MemoryPackSerializerOptions.Default);
        var output = new MemoryPackWriter<IBufferWriter<byte>>(ref writer, state);
        var tmp = ReusableLinkedArrayBufferWriterPool.Rent();
        try
        {
            Span<int> offsets = stackalloc int[EnvelopeMembers];
            var tw = new MemoryPackWriter<ReusableLinkedArrayBufferWriter>(ref tmp, state);
            tw.WriteUnmanaged(envelope.V);
            offsets[0] = tw.WrittenCount;
            tw.WriteString(envelope.T);
            offsets[1] = tw.WrittenCount;
            tw.WriteUnmanaged((byte)envelope.K);
            offsets[2] = tw.WrittenCount;
            if (envelope.Id is not null) tw.WriteString(envelope.Id);
            offsets[3] = tw.WrittenCount;
            if (envelope.Re is not null) tw.WriteString(envelope.Re);
            offsets[4] = tw.WrittenCount;
            if (envelope.S is not null) tw.WriteString(envelope.S);
            offsets[5] = tw.WrittenCount;
            if (envelope.N is { } n) tw.WriteUnmanaged(n);
            offsets[6] = tw.WrittenCount;
            if (envelope.Ts is { } ts) MemoryPackWireRuntime.WriteDateTimeOffset(ref tw, ts);
            offsets[7] = tw.WrittenCount;
            if (!envelope.P.IsEmpty) tw.WriteUnmanagedSpan(envelope.P.Span);
            offsets[8] = tw.WrittenCount;
            tw.Flush();

            output.WriteObjectHeader(EnvelopeMembers);
            for (var i = 0; i < EnvelopeMembers; i++) output.WriteVarInt(i == 0 ? offsets[0] : offsets[i] - offsets[i - 1]);
            tmp.WriteToAndReset(ref output);
            output.Flush();
        }
        finally
        {
            ReusableLinkedArrayBufferWriterPool.Return(tmp);
        }
    }

    /// <inheritdoc />
    public Envelope Decode(ReadOnlySpan<byte> frame)
    {
        if (frame.IsEmpty) throw new WireFormatException("Frame is empty.");
        try
        {
            return DecodeCore(frame);
        }
        catch (Exception ex) when (IsDecodeFailure(ex))
        {
            throw new WireFormatException("Frame is not a valid MemoryPack envelope.", ex);
        }
    }

    /// <inheritdoc />
    public ReadOnlyMemory<byte> EncodePayload<T>(T payload)
    {
        if (!WireMemoryPackPayloads.IsRegistered<T>()) throw new NotSupportedException($"{typeof(T).Name} is not a registered wire payload.");
        var buffer = new ArrayBufferWriter<byte>();
        using var state = MemoryPackWriterOptionalStatePool.Rent(MemoryPackSerializerOptions.Default);
        var writer = new MemoryPackWriter<ArrayBufferWriter<byte>>(ref buffer, state);
        WireMemoryPackPayloads.Write(ref writer, payload);
        writer.Flush();
        return buffer.WrittenMemory;
    }

    /// <inheritdoc />
    public T DecodePayload<T>(in Envelope envelope)
    {
        if (!WireMemoryPackPayloads.IsRegistered<T>()) throw new NotSupportedException($"{typeof(T).Name} is not a registered wire payload.");
        if (envelope.P.IsEmpty) throw new WireFormatException("Message has no payload.");
        try
        {
            using var state = MemoryPackReaderOptionalStatePool.Rent(MemoryPackSerializerOptions.Default);
            var reader = new MemoryPackReader(envelope.P.Span, state);
            var payload = WireMemoryPackPayloads.Read<T>(ref reader, _limits);
            EnsureFinished(ref reader);
            return payload;
        }
        catch (Exception ex) when (IsDecodeFailure(ex))
        {
            throw new WireFormatException("Payload does not match the message type.", ex);
        }
    }

    private Envelope DecodeCore(ReadOnlySpan<byte> frame)
    {
        using var state = MemoryPackReaderOptionalStatePool.Rent(MemoryPackSerializerOptions.Default);
        var r = new MemoryPackReader(frame, state);
        MemoryPackWireRuntime.EnterContainer(1, _limits);
        if (!r.TryReadObjectHeader(out var count)) throw MemoryPackWireRuntime.NullObject();
        Span<int> d = stackalloc int[count];
        MemoryPackWireRuntime.ReadMemberLengths(ref r, d);

        int? v = null;
        string? t = null;
        var kind = byte.MaxValue;
        string? id = null, re = null, s = null;
        long? n = null;
        DateTimeOffset? ts = null;
        var payloadStart = 0;
        var payloadLength = 0;

        // A member whose length is 0 is absent. Members past the ninth were added by a later minor version and are skipped by length.
        for (var i = 0; i < count; i++)
        {
            if (d[i] == 0) continue;
            var start = r.Consumed;
            switch (i)
            {
                case 0: v = r.ReadUnmanaged<int>(); break;
                case 1: t = MemoryPackWireRuntime.ReadString(ref r, _limits, false); break;
                case 2: kind = r.ReadUnmanaged<byte>(); break;
                case 3: id = MemoryPackWireRuntime.ReadString(ref r, _limits, true); break;
                case 4: re = MemoryPackWireRuntime.ReadString(ref r, _limits, true); break;
                case 5: s = MemoryPackWireRuntime.ReadString(ref r, _limits, true); break;
                case 6: n = r.ReadUnmanaged<long>(); break;
                case 7: ts = MemoryPackWireRuntime.ReadDateTimeOffset(ref r); break;
                case 8:
                    // The payload is opaque here: remember where its bytes are. The array header is the 4-byte count of the object bytes.
                    payloadLength = r.ReadUnmanaged<int>();
                    if (payloadLength < 0 || payloadLength != d[i] - sizeof(int)) throw new WireFormatException("The payload length disagrees with its member length.");
                    payloadStart = r.Consumed;
                    r.Advance(payloadLength);
                    break;
                default: r.Advance(d[i]); break;
            }

            MemoryPackWireRuntime.EndMember(ref r, start, d[i]);
        }

        EnsureFinished(ref r);
        if (v is null) throw new WireFormatException("Envelope has no 'v'.");
        if (string.IsNullOrEmpty(t)) throw new WireFormatException("Envelope has no 't'.");
        if (kind > (byte)EnvelopeKind.Error) throw new WireFormatException("Envelope 'k' is missing or unknown.");

        // Copy the payload out of the caller's frame: the span is not ours to keep.
        var p = payloadLength == 0 ? default : (ReadOnlyMemory<byte>)frame.Slice(payloadStart, payloadLength).ToArray();
        return new Envelope(v.Value, t, (EnvelopeKind)kind, id, re, s, n, ts, p);
    }

    private static bool IsDecodeFailure(Exception ex) =>
        ex is MemoryPackSerializationException
            or InvalidOperationException
            or OverflowException
            or ArgumentException
            or FormatException
            or InvalidCastException
            or IndexOutOfRangeException
            or InsufficientMemoryException
            or WireFormatException;

    private static void EnsureFinished(ref MemoryPackReader reader)
    {
        if (reader.Remaining != 0) throw new WireFormatException("Data follows the MemoryPack object.");
    }
}
