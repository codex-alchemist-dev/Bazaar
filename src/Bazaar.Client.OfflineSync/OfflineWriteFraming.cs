using System.Buffers.Binary;
using System.Text;
using Bazaar.OfflineSync;

namespace Bazaar.Client.OfflineSync;

/// <summary>
/// Hand-written binary framing of one <see cref="OfflineWrite"/> for the queue. No reflection and no serializer: little-endian
/// fields in a fixed order, with a leading version byte. The frame is encrypted as a whole by the queue.
/// </summary>
internal static class OfflineWriteFraming
{
    private const byte _version = 1;
    private const int _guidBytes = 16;
    private const int _fixedHeaderBytes = 1 + _guidBytes + _guidBytes + 8 + 1;

    /// <summary>Encodes a write into a frame.</summary>
    public static byte[] Encode(OfflineWrite write)
    {
        var target = Encoding.UTF8.GetBytes(write.Target);
        var baseVersion = write.BaseVersion is null ? Array.Empty<byte>() : Encoding.UTF8.GetBytes(write.BaseVersion);
        if (target.Length > ushort.MaxValue || baseVersion.Length > ushort.MaxValue)
        {
            throw new ArgumentException("The target and base version must each encode to at most 65535 bytes.", nameof(write));
        }

        var payload = write.Payload.Span;
        var size = _fixedHeaderBytes
            + 2 + target.Length
            + 1 + (write.BaseVersion is null ? 0 : 2 + baseVersion.Length)
            + 8 + 4 + _guidBytes
            + 4 + payload.Length;

        var frame = new byte[size];
        var span = frame.AsSpan();
        var offset = 0;

        span[offset++] = _version;
        WriteGuid(span, ref offset, write.WriteId);
        WriteGuid(span, ref offset, write.DeviceId);
        BinaryPrimitives.WriteInt64LittleEndian(span[offset..], write.DeviceSequence);
        offset += 8;
        span[offset++] = (byte)write.DataClass;

        BinaryPrimitives.WriteUInt16LittleEndian(span[offset..], (ushort)target.Length);
        offset += 2;
        target.CopyTo(span[offset..]);
        offset += target.Length;

        if (write.BaseVersion is null)
        {
            span[offset++] = 0;
        }
        else
        {
            span[offset++] = 1;
            BinaryPrimitives.WriteUInt16LittleEndian(span[offset..], (ushort)baseVersion.Length);
            offset += 2;
            baseVersion.CopyTo(span[offset..]);
            offset += baseVersion.Length;
        }

        BinaryPrimitives.WriteInt64LittleEndian(span[offset..], write.Hlc.WallMillis);
        offset += 8;
        BinaryPrimitives.WriteInt32LittleEndian(span[offset..], write.Hlc.Counter);
        offset += 4;
        WriteGuid(span, ref offset, write.Hlc.DeviceId);

        BinaryPrimitives.WriteInt32LittleEndian(span[offset..], payload.Length);
        offset += 4;
        payload.CopyTo(span[offset..]);

        return frame;
    }

    /// <summary>Decodes a frame. Throws <see cref="FormatException"/> for anything malformed.</summary>
    public static OfflineWrite Decode(ReadOnlySpan<byte> frame)
    {
        var reader = new FrameReader(frame);
        if (reader.ReadByte() != _version)
        {
            throw new FormatException("Unknown write frame version.");
        }

        var writeId = reader.ReadGuid();
        var deviceId = reader.ReadGuid();
        var sequence = reader.ReadInt64();

        var dataClassByte = reader.ReadByte();
        if (!Enum.IsDefined((OfflineDataClass)dataClassByte))
        {
            throw new FormatException("Unknown data class.");
        }

        var target = reader.ReadString();
        var hasBase = reader.ReadByte();
        string? baseVersion = hasBase switch
        {
            0 => null,
            1 => reader.ReadString(),
            _ => throw new FormatException("Invalid base version flag."),
        };

        var wall = reader.ReadInt64();
        var counter = reader.ReadInt32();
        var hlcDevice = reader.ReadGuid();

        var payloadLength = reader.ReadInt32();
        if (payloadLength < 0)
        {
            throw new FormatException("Negative payload length.");
        }

        var payload = reader.ReadBytes(payloadLength).ToArray();
        if (!reader.IsAtEnd)
        {
            throw new FormatException("Trailing bytes after the write.");
        }

        return new OfflineWrite(
            writeId,
            deviceId,
            sequence,
            (OfflineDataClass)dataClassByte,
            target,
            baseVersion,
            payload,
            new Hlc(wall, counter, hlcDevice));
    }

    private static void WriteGuid(Span<byte> span, ref int offset, Guid value)
    {
        value.ToByteArray().CopyTo(span[offset..]);
        offset += _guidBytes;
    }

    private ref struct FrameReader(ReadOnlySpan<byte> source)
    {
        private ReadOnlySpan<byte> _rest = source;

        public readonly bool IsAtEnd => _rest.IsEmpty;

        public ReadOnlySpan<byte> ReadBytes(int count)
        {
            if (count < 0 || count > _rest.Length)
            {
                throw new FormatException("Truncated write frame.");
            }

            var taken = _rest[..count];
            _rest = _rest[count..];
            return taken;
        }

        public byte ReadByte() => ReadBytes(1)[0];

        public int ReadInt32() => BinaryPrimitives.ReadInt32LittleEndian(ReadBytes(4));

        public long ReadInt64() => BinaryPrimitives.ReadInt64LittleEndian(ReadBytes(8));

        public Guid ReadGuid() => new(ReadBytes(_guidBytes));

        public string ReadString()
        {
            var length = BinaryPrimitives.ReadUInt16LittleEndian(ReadBytes(2));
            return Encoding.UTF8.GetString(ReadBytes(length));
        }
    }
}
