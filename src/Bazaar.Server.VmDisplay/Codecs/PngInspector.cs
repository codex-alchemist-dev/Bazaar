using System.Buffers.Binary;
using Bazaar.Server.VmDisplay.Abstractions;

namespace Bazaar.Server.VmDisplay.Codecs;

/// <summary>What the IHDR chunk of a PNG declares.</summary>
internal readonly record struct PngInfo(int Width, int Height, int BitDepth, int ColorType, int Interlace);

/// <summary>
/// Validates a PNG's structure without decoding pixels: signature, IHDR, chunk bounds and CRCs, IEND at the exact end, and the
/// declared size against the ceiling. A frame that passes can go to a caller untouched when it is within the size limits.
/// </summary>
internal static class PngInspector
{
    private static readonly byte[] _signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>Checks <paramref name="data"/> and returns what IHDR declares.</summary>
    /// <exception cref="DisplayException"><see cref="DisplayFailure.FrameTooLarge"/> or <see cref="DisplayFailure.FrameMalformed"/>.</exception>
    public static PngInfo Inspect(ReadOnlySpan<byte> data, int maxEncodedBytes)
    {
        if (data.Length > maxEncodedBytes)
            throw new DisplayException(DisplayFailure.FrameTooLarge, $"The PNG is {data.Length} bytes, above the {maxEncodedBytes} byte limit.");
        // Signature (8) + IHDR chunk (25) + IEND chunk (12).
        if (data.Length < 8 + 25 + 12 || !data[..8].SequenceEqual(_signature))
            throw Malformed("The data is not a PNG.");

        // IHDR must come first and carry 13 bytes. Dimensions are checked before any walk or allocation.
        if (BinaryPrimitives.ReadUInt32BigEndian(data[8..]) != 13 || !data.Slice(12, 4).SequenceEqual("IHDR"u8))
            throw Malformed("The first PNG chunk is not a 13 byte IHDR.");
        uint w = BinaryPrimitives.ReadUInt32BigEndian(data[16..]);
        uint h = BinaryPrimitives.ReadUInt32BigEndian(data[20..]);
        DimensionGuard.Check(w, h);
        var info = new PngInfo((int)w, (int)h, data[24], data[25], data[28]);

        int pos = 8;
        bool sawIdat = false;
        bool first = true;
        while (true)
        {
            if (data.Length - pos < 12)
                throw Malformed("The PNG ends inside a chunk header.");
            uint len = BinaryPrimitives.ReadUInt32BigEndian(data[pos..]);
            if (len > (uint)(data.Length - pos - 12))
                throw Malformed("A PNG chunk runs past the end of the data.");
            ReadOnlySpan<byte> typeAndData = data.Slice(pos + 4, (int)len + 4);
            uint stored = BinaryPrimitives.ReadUInt32BigEndian(data[(pos + 8 + (int)len)..]);
            if (Crc32.Compute(typeAndData) != stored)
                throw Malformed("A PNG chunk has a bad checksum.");
            ReadOnlySpan<byte> type = typeAndData[..4];
            int next = pos + 12 + (int)len;
            if (type.SequenceEqual("IDAT"u8))
                sawIdat = true;
            else if (type.SequenceEqual("IEND"u8))
            {
                if (len != 0 || next != data.Length || !sawIdat)
                    throw Malformed("The PNG has no image data or trailing bytes after IEND.");
                return info;
            }
            else if (first && !type.SequenceEqual("IHDR"u8))
                throw Malformed("The first PNG chunk is not IHDR.");
            first = false;
            pos = next;
        }
    }

    private static DisplayException Malformed(string why) => new(DisplayFailure.FrameMalformed, why);
}
