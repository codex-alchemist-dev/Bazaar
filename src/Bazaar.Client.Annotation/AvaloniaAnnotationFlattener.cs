using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace Bazaar.Client.Annotation;

/// <summary>
/// Draws the marks onto the captured image with Avalonia's own renderer and saves the result as PNG. It needs a rendering
/// backend (Skia in the heads and in the headless tests). The drawing runs on a worker thread, so a large photo does not
/// block the UI thread. A bitmap that cannot be decoded, or is larger than <see cref="AnnotationOptions.MaxPixelCount"/>,
/// throws, and the session reports it as a render failure.
/// </summary>
public sealed class AvaloniaAnnotationFlattener : IAnnotationFlattener
{
    private const double _dpi = 96;
    private readonly AnnotationOptions _options;

    /// <param name="options">Carries the pixel ceiling and the minimum drawn sizes.</param>
    public AvaloniaAnnotationFlattener(AnnotationOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
    }

    /// <inheritdoc />
    public Task<FlattenedImage> FlattenAsync(CapturedImage source, AnnotationDocument annotations, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(annotations);
        cancellationToken.ThrowIfCancellationRequested();

        // Snapshot the items first, so a later edit on the UI thread cannot change what this draws.
        var items = annotations.Items.ToArray();
        return Task.Run(() => Draw(source, items, cancellationToken), cancellationToken);
    }

    private FlattenedImage Draw(CapturedImage source, AnnotationItem[] items, CancellationToken cancellationToken)
    {
        using var input = new MemoryStream(source.Bytes.ToArray(), writable: false);
        using var bitmap = new Bitmap(input);

        var size = bitmap.PixelSize;
        if ((long)size.Width * size.Height > _options.MaxPixelCount || size.Width < 1 || size.Height < 1)
        {
            throw new InvalidOperationException("The image size is outside the drawable range.");
        }

        var shorterSide = Math.Min(size.Width, size.Height);
        using var target = new RenderTargetBitmap(size, new Vector(_dpi, _dpi));
        using (var context = target.CreateDrawingContext())
        {
            context.DrawImage(bitmap, new Rect(0, 0, size.Width, size.Height));

            foreach (var item in items)
            {
                cancellationToken.ThrowIfCancellationRequested();
                switch (item)
                {
                    case StrokeItem stroke:
                        DrawStroke(context, stroke, size, shorterSide);
                        break;
                    case TextItem text:
                        DrawText(context, text, size, shorterSide);
                        break;
                }
            }
        }

        using var output = new MemoryStream();
        target.Save(output, new PngBitmapEncoderOptions());
        return new FlattenedImage(output.ToArray(), size.Width, size.Height);
    }

    private static void DrawStroke(DrawingContext context, StrokeItem stroke, PixelSize size, int shorterSide)
    {
        var pen = new Pen(new SolidColorBrush(Color.FromUInt32(stroke.Argb)), Math.Max(1, stroke.WidthFraction * shorterSide))
        {
            LineCap = PenLineCap.Round,
            LineJoin = PenLineJoin.Round,
        };

        var geometry = new StreamGeometry();
        using (var figure = geometry.Open())
        {
            figure.BeginFigure(ToPixels(stroke.Points[0], size), isFilled: false);
            for (var i = 1; i < stroke.Points.Count; i++)
            {
                figure.LineTo(ToPixels(stroke.Points[i], size));
            }
            figure.EndFigure(isClosed: false);
        }
        context.DrawGeometry(null, pen, geometry);
    }

    private static void DrawText(DrawingContext context, TextItem text, PixelSize size, int shorterSide)
    {
        var formatted = new FormattedText(
            text.Text,
            System.Globalization.CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            Typeface.Default,
            Math.Max(8, text.SizeFraction * shorterSide),
            new SolidColorBrush(Color.FromUInt32(text.Argb)));
        context.DrawText(formatted, ToPixels(text.At, size));
    }

    private static Point ToPixels(NormPoint point, PixelSize size) => new(point.X * size.Width, point.Y * size.Height);
}
