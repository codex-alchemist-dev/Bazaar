using System.Runtime.InteropServices;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;

namespace Bazaar.Client.Annotation.Tests;

/// <summary>The flattener draws marks into real pixels and returns a PNG. It runs headless with Skia.</summary>
public class AvaloniaAnnotationFlattenerTests
{
    private const uint _red = 0xFFFF0000;

    [AvaloniaFact]
    public async Task A_stroke_is_drawn_into_the_flat_png()
    {
        var options = new AnnotationOptions(InkArgb: _red, StrokeWidthFraction: 0.2);
        var doc = new AnnotationDocument(options);
        Assert.True(doc.TryAddStroke([new NormPoint(0.1, 0.5), new NormPoint(0.9, 0.5)]));

        var flat = await new AvaloniaAnnotationFlattener(options).FlattenAsync(TestImages.WhitePng(20, 20), doc, TestContext.Current.CancellationToken);

        Assert.Equal(20, flat.Width);
        Assert.Equal(20, flat.Height);
        Assert.Equal(new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G' }, flat.Bytes.Span[..4].ToArray());

        // The middle of the stroke (y = 10 of 20) is red; a corner far from it stays white.
        var (r, g, b) = PixelAt(flat.Bytes, 20, 10, 10);
        Assert.True(r > 200 && g < 80 && b < 80, $"stroke pixel was {r},{g},{b}");
        var (cr, cg, cb) = PixelAt(flat.Bytes, 20, 1, 1);
        Assert.True(cr > 240 && cg > 240 && cb > 240, $"corner pixel was {cr},{cg},{cb}");
    }

    [AvaloniaFact]
    public async Task Text_is_drawn_without_failing()
    {
        var options = new AnnotationOptions();
        var doc = new AnnotationDocument(options);
        Assert.True(doc.TryAddText(new NormPoint(0.2, 0.2), "hello"));

        var flat = await new AvaloniaAnnotationFlattener(options).FlattenAsync(TestImages.WhitePng(64, 64), doc, TestContext.Current.CancellationToken);

        Assert.Equal(64, flat.Width);
        Assert.True(flat.Bytes.Length > 0);
    }

    [AvaloniaFact]
    public async Task An_image_over_the_pixel_limit_is_refused()
    {
        var options = new AnnotationOptions(MaxPixelCount: 100);
        var doc = new AnnotationDocument(options);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new AvaloniaAnnotationFlattener(options).FlattenAsync(TestImages.WhitePng(20, 20), doc, TestContext.Current.CancellationToken));
    }

    [AvaloniaFact]
    public async Task Bytes_that_are_not_an_image_are_refused()
    {
        var options = new AnnotationOptions();
        var bad = new CapturedImage(new byte[] { 1, 2, 3, 4, 5 }, "image/png");

        await Assert.ThrowsAnyAsync<Exception>(() =>
            new AvaloniaAnnotationFlattener(options).FlattenAsync(bad, new AnnotationDocument(options), TestContext.Current.CancellationToken));
    }

    /// <summary>Reads one pixel of a PNG as red, green and blue.</summary>
    private static (byte R, byte G, byte B) PixelAt(ReadOnlyMemory<byte> png, int width, int x, int y)
    {
        using var stream = new MemoryStream(png.ToArray(), writable: false);
        using var bitmap = WriteableBitmap.DecodeToWidth(stream, width, BitmapInterpolationMode.None);
        using var frame = bitmap.Lock();
        var offset = y * frame.RowBytes + x * 4;
        // Bgra8888: blue, green, red, alpha.
        return (Marshal.ReadByte(frame.Address, offset + 2), Marshal.ReadByte(frame.Address, offset + 1), Marshal.ReadByte(frame.Address, offset));
    }
}
