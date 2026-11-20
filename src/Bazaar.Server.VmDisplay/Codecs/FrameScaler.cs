namespace Bazaar.Server.VmDisplay.Codecs;

/// <summary>Box-filter downscaling that keeps the aspect ratio.</summary>
internal static class FrameScaler
{
    /// <summary>The size <paramref name="width"/> x <paramref name="height"/> becomes when fitted inside the limits (never upscaled).</summary>
    public static (int Width, int Height) FitSize(int width, int height, int maxWidth, int maxHeight)
    {
        if (width <= maxWidth && height <= maxHeight)
            return (width, height);
        double ratio = Math.Min((double)maxWidth / width, (double)maxHeight / height);
        int w = Math.Clamp((int)Math.Floor(width * ratio), 1, maxWidth);
        int h = Math.Clamp((int)Math.Floor(height * ratio), 1, maxHeight);
        return (w, h);
    }

    /// <summary>Returns <paramref name="source"/> when it already fits, otherwise a downscaled copy.</summary>
    public static PixelBuffer FitWithin(PixelBuffer source, int maxWidth, int maxHeight)
    {
        (int w, int h) = FitSize(source.Width, source.Height, maxWidth, maxHeight);
        if (w == source.Width && h == source.Height)
            return source;

        int ch = source.Channels;
        var dst = new byte[w * h * ch];
        Span<long> sums = stackalloc long[4];
        for (int y = 0; y < h; y++)
        {
            int sy0 = (int)((long)y * source.Height / h);
            int sy1 = Math.Max(sy0 + 1, (int)((long)(y + 1) * source.Height / h));
            for (int x = 0; x < w; x++)
            {
                int sx0 = (int)((long)x * source.Width / w);
                int sx1 = Math.Max(sx0 + 1, (int)((long)(x + 1) * source.Width / w));
                sums.Clear();
                for (int sy = sy0; sy < sy1; sy++)
                {
                    int rowBase = sy * source.Stride;
                    for (int sx = sx0; sx < sx1; sx++)
                    {
                        int idx = rowBase + (sx * ch);
                        for (int c = 0; c < ch; c++)
                            sums[c] += source.Data[idx + c];
                    }
                }
                long count = (long)(sy1 - sy0) * (sx1 - sx0);
                int o = ((y * w) + x) * ch;
                for (int c = 0; c < ch; c++)
                    dst[o + c] = (byte)((sums[c] + (count / 2)) / count);
            }
        }
        return new PixelBuffer(w, h, ch, dst);
    }
}
