using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;

namespace Bazaar.Client.Core.Tests.Headless;

public class FrameCaptureTests
{
    [AvaloniaFact]
    public void Capture_returns_a_png_of_the_requested_size()
    {
        var frame = FrameCapture.Capture(new Border { Background = Brushes.Red }, 64, 32);

        Assert.Equal(64, frame.Width);
        Assert.Equal(32, frame.Height);
        Assert.Equal((64, 32), FrameCapture.ReadPngSize(frame.Png));
    }

    [AvaloniaFact]
    public void Capture_of_the_same_control_gives_the_same_hash()
    {
        var first = FrameCapture.Capture(new Border { Background = Brushes.Teal }, 40, 40);
        var second = FrameCapture.Capture(new Border { Background = Brushes.Teal }, 40, 40);

        Assert.Equal(first.Sha256, second.Sha256);
        Assert.Equal(first.Png, second.Png);
    }

    [AvaloniaFact]
    public void Capture_detects_a_changed_pixel_colour()
    {
        var red = FrameCapture.Capture(new Border { Background = Brushes.Red }, 40, 40);
        var blue = FrameCapture.Capture(new Border { Background = Brushes.Blue }, 40, 40);

        Assert.NotEqual(red.Sha256, blue.Sha256);
    }

    [AvaloniaFact]
    public void Capture_rejects_a_non_positive_size()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => FrameCapture.Capture(new Border(), 0, 10));
    }

    [AvaloniaFact]
    public void Capture_rejects_a_null_control()
    {
        Assert.Throws<ArgumentNullException>(() => FrameCapture.Capture(null!, 10, 10));
    }

    [Fact]
    public void ReadPngSize_reads_the_ihdr_dimensions()
    {
        // 8-byte signature, IHDR length 13, "IHDR", width 3, height 5.
        byte[] png =
        [
            0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A,
            0x00, 0x00, 0x00, 0x0D, 0x49, 0x48, 0x44, 0x52,
            0x00, 0x00, 0x00, 0x03, 0x00, 0x00, 0x00, 0x05,
        ];

        Assert.Equal((3, 5), FrameCapture.ReadPngSize(png));
    }

    [Fact]
    public void ReadPngSize_returns_null_for_bytes_that_are_not_a_png()
    {
        Assert.Null(FrameCapture.ReadPngSize(new byte[24]));
        Assert.Null(FrameCapture.ReadPngSize([1, 2, 3]));
    }
}
