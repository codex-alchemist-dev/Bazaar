using System.Security.Cryptography;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;

namespace Bazaar.Client.Core.Tests.Headless;

/// <summary>One rendered frame: its pixel size, its PNG bytes and a SHA-256 of those bytes.</summary>
public sealed record CapturedFrame(int Width, int Height, byte[] Png)
{
    /// <summary>Lower-case hex SHA-256 of <see cref="Png"/>. Equal hashes mean identical pixels and encoding.</summary>
    public string Sha256 => Convert.ToHexString(SHA256.HashData(Png)).ToLowerInvariant();
}

/// <summary>
/// Renders a control through the Avalonia headless platform and returns the frame as PNG bytes.
/// The control is hosted in a window of the given size, so the frame is the control at that size.
/// Call from an <c>[AvaloniaFact]</c> test, which runs on the UI thread.
/// </summary>
public static class FrameCapture
{
    private static readonly byte[] _pngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    public static CapturedFrame Capture(Control control, double width, double height)
    {
        ArgumentNullException.ThrowIfNull(control);
        if (width <= 0 || height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "A frame needs a positive size.");
        }

        var window = new Window
        {
            Width = width,
            Height = height,
            Content = control,
        };

        try
        {
            window.Show();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            using var bitmap = window.CaptureRenderedFrame()
                ?? throw new InvalidOperationException("The headless platform produced no frame.");

            using var stream = new MemoryStream();
            bitmap.Save(stream, new PngBitmapEncoderOptions());
            return new CapturedFrame(bitmap.PixelSize.Width, bitmap.PixelSize.Height, stream.ToArray());
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>Reads the pixel width and height from a PNG header, or returns null if the bytes are not a PNG.</summary>
    public static (int Width, int Height)? ReadPngSize(byte[] png)
    {
        ArgumentNullException.ThrowIfNull(png);
        if (png.Length < 24 || !png.AsSpan(0, 8).SequenceEqual(_pngSignature))
        {
            return null;
        }

        // The IHDR chunk is always first: length (4), type "IHDR" (4), then width and height as big-endian uint32.
        if (!png.AsSpan(12, 4).SequenceEqual("IHDR"u8))
        {
            return null;
        }

        int width = (int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(16, 4));
        int height = (int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(20, 4));
        return (width, height);
    }
}
