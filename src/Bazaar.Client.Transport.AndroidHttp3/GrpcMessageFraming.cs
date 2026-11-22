using System.Buffers.Binary;

namespace Bazaar.Client.Transport.AndroidHttp3;

/// <summary>
/// gRPC message framing by hand, over a raw HTTP/3 body: a 5-byte prefix (compressed flag, big-endian length) around a
/// protobuf <c>Frame { bytes data = 1 }</c>. The same bytes the <c>grpc</c> module puts on the wire (doc 90 section 9).
/// </summary>
internal static class GrpcMessageFraming
{
    public const int PrefixBytes = 5;
    public const int FieldHeaderAllowance = 16;

    /// <summary>Encodes one codec frame as one gRPC message.</summary>
    public static byte[] Encode(ReadOnlySpan<byte> frame)
    {
        var varintSize = VarintSize((uint)frame.Length);
        var messageLength = frame.Length == 0 ? 0 : 1 + varintSize + frame.Length;
        var result = new byte[PrefixBytes + messageLength];
        result[0] = 0;
        BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(1, 4), (uint)messageLength);
        if (frame.Length > 0)
        {
            var at = PrefixBytes;
            result[at++] = 0x0A;
            var length = (uint)frame.Length;
            while (length >= 0x80)
            {
                result[at++] = (byte)(length | 0x80);
                length >>= 7;
            }

            result[at++] = (byte)length;
            frame.CopyTo(result.AsSpan(at));
        }

        return result;
    }

    /// <summary>
    /// Reads the declared message length from a prefix. False on a compressed message or a length above the limit, so
    /// the caller closes before buffering the body.
    /// </summary>
    public static bool TryReadPrefix(ReadOnlySpan<byte> prefix, int maxMessageBytes, out int messageLength)
    {
        messageLength = 0;
        if (prefix[0] != 0) return false;
        var length = BinaryPrimitives.ReadUInt32BigEndian(prefix.Slice(1, 4));
        if (length > (uint)maxMessageBytes) return false;
        messageLength = (int)length;
        return true;
    }

    /// <summary>Extracts the <c>data</c> field of a <c>Frame</c> message, skipping unknown fields. False when malformed or the data is above the limit.</summary>
    public static bool TryDecode(ReadOnlySpan<byte> message, int maxFrameBytes, out byte[] frame)
    {
        frame = [];
        var at = 0;
        while (at < message.Length)
        {
            if (!TryReadVarint(message, ref at, out var tag)) return false;
            switch (tag & 7)
            {
                case 0:
                    if (!TryReadVarint(message, ref at, out _)) return false;
                    break;
                case 1:
                    if (message.Length - at < 8) return false;
                    at += 8;
                    break;
                case 5:
                    if (message.Length - at < 4) return false;
                    at += 4;
                    break;
                case 2:
                    if (!TryReadVarint(message, ref at, out var length)) return false;
                    if (length > (ulong)(message.Length - at)) return false;
                    if (tag >> 3 == 1)
                    {
                        if (length > (ulong)maxFrameBytes) return false;
                        frame = message.Slice(at, (int)length).ToArray();
                    }

                    at += (int)length;
                    break;
                default:
                    return false;
            }
        }

        return true;
    }

    private static int VarintSize(uint value)
    {
        var size = 1;
        while (value >= 0x80)
        {
            value >>= 7;
            size++;
        }

        return size;
    }

    private static bool TryReadVarint(ReadOnlySpan<byte> span, ref int at, out ulong value)
    {
        value = 0;
        for (var shift = 0; shift < 64 && at < span.Length; shift += 7)
        {
            var b = span[at++];
            value |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0) return true;
        }

        return false;
    }
}
