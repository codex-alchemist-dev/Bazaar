using Bazaar.Server.VmDisplay.Abstractions;

namespace Bazaar.Server.VmDisplay.Codecs;

/// <summary>The size limits a frame is held to.</summary>
internal readonly record struct FrameLimits(int MaxWidth, int MaxHeight, int MaxEncodedBytes);

/// <summary>A validated PNG with its dimensions.</summary>
internal readonly record struct NormalizedFrame(byte[] Png, int Width, int Height);

/// <summary>
/// Turns whatever a source delivered into a validated PNG within the limits. A PNG already within the limits passes through
/// untouched after header validation; other inputs are converted, and anything too large is downscaled.
/// </summary>
internal static class FrameNormalizer
{
    /// <summary>Validates a PNG, downscaling it when it is larger than the limits.</summary>
    public static NormalizedFrame FromPng(byte[] data, FrameLimits limits)
    {
        if (data is null)
            throw new DisplayException(DisplayFailure.FrameMalformed, "The source returned no data.");
        PngInfo info = PngInspector.Inspect(data, limits.MaxEncodedBytes);
        if (info.Width <= limits.MaxWidth && info.Height <= limits.MaxHeight)
            return new NormalizedFrame(data, info.Width, info.Height);
        return Finish(PngDecoder.Decode(data, limits.MaxEncodedBytes), limits);
    }

    /// <summary>Converts a raw frame to a PNG within the limits.</summary>
    public static NormalizedFrame FromRaw(RawFrame raw, FrameLimits limits)
    {
        if (raw?.Data is null)
            throw new DisplayException(DisplayFailure.FrameMalformed, "The source returned no frame.");
        if (raw.Data.Length > limits.MaxEncodedBytes && raw.Format is RawPixelFormat.Png or RawPixelFormat.Ppm)
            throw new DisplayException(DisplayFailure.FrameTooLarge, "The frame is larger than the byte limit.");
        return raw.Format switch
        {
            RawPixelFormat.Png => FromPng(raw.Data, limits),
            RawPixelFormat.Rgb565 => Finish(Rgb565Converter.ToRgb(raw.Data, raw.Width, raw.Height), limits),
            RawPixelFormat.Bgra32 => Finish(Bgra32Converter.ToRgb(raw.Data, raw.Width, raw.Height), limits),
            RawPixelFormat.Ppm => Finish(PpmParser.Parse(raw.Data), limits),
            _ => throw new DisplayException(DisplayFailure.FrameMalformed, "Unknown frame format."),
        };
    }

    private static NormalizedFrame Finish(PixelBuffer pixels, FrameLimits limits)
    {
        PixelBuffer fitted = FrameScaler.FitWithin(pixels, limits.MaxWidth, limits.MaxHeight);
        byte[] png = PngEncoder.Encode(fitted);
        if (png.Length > limits.MaxEncodedBytes)
            throw new DisplayException(DisplayFailure.FrameTooLarge, "The encoded frame is larger than the byte limit.");
        return new NormalizedFrame(png, fitted.Width, fitted.Height);
    }
}
