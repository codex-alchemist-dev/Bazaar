using System.Buffers.Binary;
using System.IO.Compression;

namespace Bazaar.Server.VmDisplay.Codecs;

/// <summary>Writes 8-bit RGB or RGBA pixels as a non-interlaced PNG using filter 0 and <see cref="ZLibStream"/>.</summary>
internal static class PngEncoder
{
    /// <summary>Encodes <paramref name="image"/>.</summary>
    public static byte[] Encode(PixelBuffer image)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (image.Channels is not (3 or 4) || image.Data.Length != (long)image.Stride * image.Height)
            throw new ArgumentException("The pixel buffer is not consistent.", nameof(image));

        using var output = new MemoryStream();
        output.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);

        Span<byte> ihdr = stackalloc byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(ihdr, (uint)image.Width);
        BinaryPrimitives.WriteUInt32BigEndian(ihdr[4..], (uint)image.Height);
        ihdr[8] = 8;
        ihdr[9] = image.Channels == 4 ? (byte)6 : (byte)2;
        ihdr[10] = 0;
        ihdr[11] = 0;
        ihdr[12] = 0;
        WriteChunk(output, "IHDR"u8, ihdr);

        using var compressed = new MemoryStream();
        using (var z = new ZLibStream(compressed, CompressionLevel.Fastest, leaveOpen: true))
        {
            int stride = image.Stride;
            for (int y = 0; y < image.Height; y++)
            {
                z.WriteByte(0);
                z.Write(image.Data, y * stride, stride);
            }
        }
        WriteChunk(output, "IDAT"u8, compressed.GetBuffer().AsSpan(0, (int)compressed.Length));
        WriteChunk(output, "IEND"u8, []);
        return output.ToArray();
    }

    private static void WriteChunk(Stream output, ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        Span<byte> word = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(word, (uint)data.Length);
        output.Write(word);
        var crcInput = new byte[4 + data.Length];
        type.CopyTo(crcInput);
        data.CopyTo(crcInput.AsSpan(4));
        output.Write(crcInput);
        BinaryPrimitives.WriteUInt32BigEndian(word, Crc32.Compute(crcInput));
        output.Write(word);
    }
}
