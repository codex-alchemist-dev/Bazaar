using Bazaar.Server.VmDisplay.Abstractions;

namespace Bazaar.Server.VmDisplay.Codecs;

/// <summary>Decoded 8-bit pixels, row-major, <see cref="Channels"/> (3 for RGB, 4 for RGBA) bytes per pixel.</summary>
internal sealed record PixelBuffer(int Width, int Height, int Channels, byte[] Data)
{
    /// <summary>Bytes in one row.</summary>
    public int Stride => Width * Channels;
}

/// <summary>Dimension checks shared by every codec, run before any allocation sized from untrusted numbers.</summary>
internal static class DimensionGuard
{
    /// <summary>Throws unless <paramref name="width"/> and <paramref name="height"/> are positive and within the hard ceiling.</summary>
    public static void Check(long width, long height)
    {
        if (width < 1 || height < 1)
            throw new DisplayException(DisplayFailure.FrameMalformed, "The frame has non-positive dimensions.");
        if (width > DisplayLimits.CeilingWidth || height > DisplayLimits.CeilingHeight)
            throw new DisplayException(DisplayFailure.FrameTooLarge, $"The frame is {width}x{height}, above the {DisplayLimits.CeilingWidth}x{DisplayLimits.CeilingHeight} ceiling.");
    }
}
