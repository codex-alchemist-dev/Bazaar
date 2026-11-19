using Bazaar.Server.VmDisplay.Abstractions;

namespace Bazaar.Server.VmDisplay.Codecs;

/// <summary>Converts little-endian RGB565 frames (Hyper-V thumbnails) to RGB.</summary>
internal static class Rgb565Converter
{
    /// <summary>Converts <paramref name="data"/>, which must be exactly width * height * 2 bytes.</summary>
    public static PixelBuffer ToRgb(ReadOnlySpan<byte> data, int width, int height)
    {
        DimensionGuard.Check(width, height);
        if (data.Length != (long)width * height * 2)
            throw new DisplayException(DisplayFailure.FrameMalformed, "The RGB565 data length does not match the declared size.");
        var rgb = new byte[width * height * 3];
        int o = 0;
        for (int i = 0; i < data.Length; i += 2)
        {
            int v = data[i] | (data[i + 1] << 8);
            int r = v >> 11;
            int g = (v >> 5) & 0x3F;
            int b = v & 0x1F;
            rgb[o++] = (byte)((r << 3) | (r >> 2));
            rgb[o++] = (byte)((g << 2) | (g >> 4));
            rgb[o++] = (byte)((b << 3) | (b >> 2));
        }
        return new PixelBuffer(width, height, 3, rgb);
    }
}

/// <summary>Converts BGRA frames to RGB. Alpha is dropped because framebuffers often leave it zero.</summary>
internal static class Bgra32Converter
{
    /// <summary>Converts <paramref name="data"/>, which must be exactly width * height * 4 bytes.</summary>
    public static PixelBuffer ToRgb(ReadOnlySpan<byte> data, int width, int height)
    {
        DimensionGuard.Check(width, height);
        if (data.Length != (long)width * height * 4)
            throw new DisplayException(DisplayFailure.FrameMalformed, "The BGRA data length does not match the declared size.");
        var rgb = new byte[width * height * 3];
        int o = 0;
        for (int i = 0; i < data.Length; i += 4)
        {
            rgb[o++] = data[i + 2];
            rgb[o++] = data[i + 1];
            rgb[o++] = data[i];
        }
        return new PixelBuffer(width, height, 3, rgb);
    }
}

/// <summary>Strict parser for binary PPM (P6, maxval 255) as QEMU writes it.</summary>
internal static class PpmParser
{
    private const int MaxHeaderBytes = 64;

    /// <summary>Parses <paramref name="data"/>. The size is checked against the data length before any pixel allocation.</summary>
    public static PixelBuffer Parse(ReadOnlySpan<byte> data)
    {
        if (data.Length < 2 || data[0] != (byte)'P' || data[1] != (byte)'6')
            throw Malformed("The data is not a binary PPM.");
        int pos = 2;
        long width = ReadNumber(data, ref pos);
        long height = ReadNumber(data, ref pos);
        long maxval = ReadNumber(data, ref pos);
        // Exactly one whitespace byte separates the header from the pixels.
        if (pos >= data.Length || !IsWhitespace(data[pos]))
            throw Malformed("The PPM header is not terminated.");
        pos++;
        if (maxval != 255)
            throw Malformed("Only PPM files with maxval 255 are supported.");
        DimensionGuard.Check(width, height);
        if (data.Length - pos != width * height * 3)
            throw Malformed("The PPM pixel data length does not match its header.");
        return new PixelBuffer((int)width, (int)height, 3, data[pos..].ToArray());
    }

    // Skips whitespace, then reads 1 to 5 digits. The header is bounded so a hostile file cannot make the parser scan far.
    private static long ReadNumber(ReadOnlySpan<byte> data, ref int pos)
    {
        while (pos < data.Length && pos < MaxHeaderBytes && IsWhitespace(data[pos]))
            pos++;
        int start = pos;
        long value = 0;
        while (pos < data.Length && pos < MaxHeaderBytes && data[pos] is >= (byte)'0' and <= (byte)'9')
        {
            if (pos - start >= 5)
                throw Malformed("A PPM header number is too long.");
            value = (value * 10) + (data[pos] - (byte)'0');
            pos++;
        }
        if (pos == start)
            throw Malformed("The PPM header is incomplete or too long.");
        return value;
    }

    private static bool IsWhitespace(byte b) => b is (byte)' ' or (byte)'\n' or (byte)'\r' or (byte)'\t';

    private static DisplayException Malformed(string why) => new(DisplayFailure.FrameMalformed, why);
}
