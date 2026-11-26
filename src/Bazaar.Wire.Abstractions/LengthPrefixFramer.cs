using System.Buffers;
using System.Buffers.Binary;

namespace Bazaar.Wire.Abstractions;

/// <summary>
/// Frames on stream transports (<c>quic</c>, <c>webtransport</c>): each frame is a QUIC variable-length
/// integer (RFC 9000 section 16) holding the byte length, then the bytes (doc 90 section 9).
/// </summary>
public static class LengthPrefixFramer
{
    /// <summary>Largest length a variable-length integer holds.</summary>
    public const long MaxVarInt = (1L << 62) - 1;

    /// <summary>Writes one frame. The length must not exceed <paramref name="maxFrameBytes"/>.</summary>
    public static void Write(ReadOnlySpan<byte> frame, IBufferWriter<byte> writer, int maxFrameBytes)
    {
        ArgumentNullException.ThrowIfNull(writer);
        if (frame.Length > maxFrameBytes) throw new ArgumentException("Frame exceeds the maximum size.", nameof(frame));
        var size = VarIntSize(frame.Length);
        var span = writer.GetSpan(size + frame.Length);
        WriteVarInt(span, frame.Length);
        frame.CopyTo(span[size..]);
        writer.Advance(size + frame.Length);
    }

    /// <summary>
    /// Tries to read one frame from the buffer. Returns false when more bytes are needed. A declared length above
    /// <paramref name="maxFrameBytes"/> throws before any payload byte is awaited, so a peer cannot make the reader buffer it.
    /// </summary>
    /// <exception cref="InvalidDataException">The declared length exceeds the maximum.</exception>
    public static bool TryRead(ref ReadOnlySequence<byte> buffer, int maxFrameBytes, out ReadOnlySequence<byte> frame)
    {
        frame = default;
        if (buffer.IsEmpty) return false;
        Span<byte> head = stackalloc byte[8];
        var first = buffer.FirstSpan[0];
        var size = 1 << (first >> 6);
        if (buffer.Length < size) return false;
        buffer.Slice(0, size).CopyTo(head);
        var length = ReadVarInt(head[..size]);
        if (length > maxFrameBytes) throw new InvalidDataException("Frame length exceeds the maximum.");
        if (buffer.Length < size + length) return false;
        frame = buffer.Slice(size, length);
        buffer = buffer.Slice(size + length);
        return true;
    }

    /// <summary>Bytes a variable-length integer needs for <paramref name="value"/>.</summary>
    public static int VarIntSize(long value) => value switch
    {
        < 0 or > MaxVarInt => throw new ArgumentOutOfRangeException(nameof(value)),
        < 64 => 1,
        < 16384 => 2,
        < 1073741824 => 4,
        _ => 8,
    };

    private static void WriteVarInt(Span<byte> span, long value)
    {
        switch (VarIntSize(value))
        {
            case 1: span[0] = (byte)value; break;
            case 2: BinaryPrimitives.WriteUInt16BigEndian(span, (ushort)(value | 0x4000)); break;
            case 4: BinaryPrimitives.WriteUInt32BigEndian(span, (uint)value | 0x80000000u); break;
            default: BinaryPrimitives.WriteUInt64BigEndian(span, (ulong)value | 0xC000000000000000UL); break;
        }
    }

    private static int ReadVarInt(ReadOnlySpan<byte> span)
    {
        long value = span.Length switch
        {
            1 => span[0],
            2 => BinaryPrimitives.ReadUInt16BigEndian(span) & 0x3FFF,
            4 => BinaryPrimitives.ReadUInt32BigEndian(span) & 0x3FFFFFFF,
            _ => (long)(BinaryPrimitives.ReadUInt64BigEndian(span) & 0x3FFFFFFFFFFFFFFFUL),
        };
        return value > int.MaxValue ? int.MaxValue : (int)value;
    }
}
