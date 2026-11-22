using System.Buffers.Binary;
using System.IO.Compression;
using Bazaar.Server.VmDisplay.Abstractions;

namespace Bazaar.Server.VmDisplay.Codecs;

/// <summary>
/// A minimal PNG decoder for downscaling: 8-bit RGB or RGBA, non-interlaced, all five filter types. Anything else is
/// <see cref="DisplayFailure.FrameMalformed"/>. The output buffer is sized from dimensions checked against the ceiling first.
/// </summary>
internal static class PngDecoder
{
    /// <summary>Decodes <paramref name="data"/>.</summary>
    public static PixelBuffer Decode(byte[] data, int maxEncodedBytes)
    {
        PngInfo info = PngInspector.Inspect(data, maxEncodedBytes);
        if (info.BitDepth != 8 || info.ColorType is not (2 or 6) || info.Interlace != 0)
            throw new DisplayException(DisplayFailure.FrameMalformed, "Only non-interlaced 8-bit RGB and RGBA PNGs can be resized.");

        int channels = info.ColorType == 6 ? 4 : 3;
        int stride = info.Width * channels;
        int rawLength = checked((stride + 1) * info.Height);

        using var idat = new MemoryStream();
        int pos = 8;
        while (pos < data.Length)
        {
            int len = (int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(pos));
            if (data.AsSpan(pos + 4, 4).SequenceEqual("IDAT"u8))
                idat.Write(data, pos + 8, len);
            pos += 12 + len;
        }

        var raw = new byte[rawLength];
        try
        {
            idat.Position = 0;
            using var z = new ZLibStream(idat, CompressionMode.Decompress);
            int got = 0;
            while (got < rawLength)
            {
                int n = z.Read(raw, got, rawLength - got);
                if (n == 0)
                    throw new DisplayException(DisplayFailure.FrameMalformed, "The PNG image data is shorter than its header declares.");
                got += n;
            }
            if (z.ReadByte() >= 0)
                throw new DisplayException(DisplayFailure.FrameMalformed, "The PNG image data is longer than its header declares.");
        }
        catch (InvalidDataException e)
        {
            throw new DisplayException(DisplayFailure.FrameMalformed, "The PNG image data is not valid zlib data.", e);
        }

        var pixels = new byte[stride * info.Height];
        for (int y = 0; y < info.Height; y++)
        {
            byte filter = raw[y * (stride + 1)];
            ReadOnlySpan<byte> src = raw.AsSpan(y * (stride + 1) + 1, stride);
            Span<byte> row = pixels.AsSpan(y * stride, stride);
            ReadOnlySpan<byte> prior = y == 0 ? default : pixels.AsSpan((y - 1) * stride, stride);
            Unfilter(filter, src, row, prior, channels);
        }
        return new PixelBuffer(info.Width, info.Height, channels, pixels);
    }

    private static void Unfilter(byte filter, ReadOnlySpan<byte> src, Span<byte> row, ReadOnlySpan<byte> prior, int bpp)
    {
        for (int i = 0; i < src.Length; i++)
        {
            int a = i >= bpp ? row[i - bpp] : 0;
            int b = prior.IsEmpty ? 0 : prior[i];
            int c = i >= bpp && !prior.IsEmpty ? prior[i - bpp] : 0;
            int add = filter switch
            {
                0 => 0,
                1 => a,
                2 => b,
                3 => (a + b) / 2,
                4 => Paeth(a, b, c),
                _ => throw new DisplayException(DisplayFailure.FrameMalformed, "The PNG uses an unknown filter type."),
            };
            row[i] = (byte)(src[i] + add);
        }
    }

    private static int Paeth(int a, int b, int c)
    {
        int p = a + b - c;
        int pa = Math.Abs(p - a);
        int pb = Math.Abs(p - b);
        int pc = Math.Abs(p - c);
        if (pa <= pb && pa <= pc)
            return a;
        return pb <= pc ? b : c;
    }
}
