using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace Bazaar.Client.Annotation;

/// <summary>
/// Shows the screenshot with its marks and turns pointer gestures into annotations. In draw mode a press, drag and release
/// adds one stroke. In text mode a press places the text from <c>textSource</c>. Both go through the session, so the
/// limits and the annotating phase apply here too. Nothing is sent from this control.
/// </summary>
public sealed class AnnotationCanvas : Control, IDisposable
{
    private const double _minPointDistance = 0.002;
    private readonly ScreenshotAnnotationSession _session;
    private readonly AnnotationOptions _options;
    private readonly Func<string> _textSource;
    private Bitmap? _bitmap;
    private CapturedImage? _bitmapSource;
    private List<NormPoint>? _stroke;

    /// <param name="session">The session whose marks this shows and edits.</param>
    /// <param name="options">The ink and pen width for the stroke in progress.</param>
    /// <param name="textSource">Returns the text to place in text mode.</param>
    public AnnotationCanvas(ScreenshotAnnotationSession session, AnnotationOptions options, Func<string> textSource)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(textSource);
        _session = session;
        _options = options;
        _textSource = textSource;
        _session.Changed += (_, _) => InvalidateVisual();
    }

    /// <summary>When true, a press places text instead of starting a stroke.</summary>
    public bool TextMode { get; set; }

    /// <summary>The image rectangle inside this control, letterboxed and centred. Default (zero size) when no image is decoded.</summary>
    public Rect ImageRect
    {
        get
        {
            if (!EnsureBitmap() || _bitmap is null) return default;

            var size = _bitmap.PixelSize;
            var scale = Math.Min(Bounds.Width / size.Width, Bounds.Height / size.Height);
            if (!double.IsFinite(scale) || scale <= 0) return default;

            var width = size.Width * scale;
            var height = size.Height * scale;
            return new Rect((Bounds.Width - width) / 2, (Bounds.Height - height) / 2, width, height);
        }
    }

    /// <inheritdoc />
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed || _session.Phase != AnnotationPhase.Annotating) return;

        var rect = ImageRect;
        if (rect.Width <= 0 || rect.Height <= 0) return;

        var point = ToNorm(e.GetPosition(this), rect);
        e.Handled = true;
        if (TextMode)
        {
            _session.TryAddText(point, _textSource());
            return;
        }

        _stroke = [point];
        e.Pointer.Capture(this);
        InvalidateVisual();
    }

    /// <inheritdoc />
    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_stroke is null) return;

        var rect = ImageRect;
        if (rect.Width <= 0 || rect.Height <= 0) return;

        var point = ToNorm(e.GetPosition(this), rect);
        var last = _stroke[^1];
        if (Math.Abs(point.X - last.X) + Math.Abs(point.Y - last.Y) < _minPointDistance) return;

        _stroke.Add(point);
        InvalidateVisual();
    }

    /// <inheritdoc />
    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_stroke is null) return;

        // Keep a stroke only if it has two points. A tap alone is ignored, as the document refuses it.
        var finished = _stroke;
        _stroke = null;
        e.Pointer.Capture(null);
        _session.TryAddStroke(finished);
        InvalidateVisual();
    }

    /// <inheritdoc />
    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var rect = ImageRect;
        if (rect.Width <= 0 || rect.Height <= 0 || _bitmap is null) return;

        context.DrawImage(_bitmap, new Rect(0, 0, _bitmap.PixelSize.Width, _bitmap.PixelSize.Height), rect);
        var shorter = Math.Min(rect.Width, rect.Height);
        var options = _session.Annotations.Items;
        foreach (var item in options)
        {
            switch (item)
            {
                case StrokeItem stroke:
                    DrawStroke(context, rect, stroke.Points, stroke.Argb, stroke.WidthFraction * shorter);
                    break;
                case TextItem text:
                    var formatted = new FormattedText(
                        text.Text,
                        System.Globalization.CultureInfo.InvariantCulture,
                        FlowDirection.LeftToRight,
                        Typeface.Default,
                        Math.Max(8, text.SizeFraction * shorter),
                        new SolidColorBrush(Color.FromUInt32(text.Argb)));
                    context.DrawText(formatted, ToScreen(text.At, rect));
                    break;
            }
        }

        if (_stroke is { Count: > 1 } preview)
        {
            DrawStroke(context, rect, preview, _options.InkArgb, _options.StrokeWidthFraction * shorter);
        }
    }

    private static void DrawStroke(DrawingContext context, Rect rect, IReadOnlyList<NormPoint> points, uint argb, double width)
    {
        var pen = new Pen(new SolidColorBrush(Color.FromUInt32(argb)), Math.Max(1, width))
        {
            LineCap = PenLineCap.Round,
            LineJoin = PenLineJoin.Round,
        };
        var geometry = new StreamGeometry();
        using (var figure = geometry.Open())
        {
            figure.BeginFigure(ToScreen(points[0], rect), isFilled: false);
            for (var i = 1; i < points.Count; i++)
            {
                figure.LineTo(ToScreen(points[i], rect));
            }
            figure.EndFigure(isClosed: false);
        }
        context.DrawGeometry(null, pen, geometry);
    }

    private static NormPoint ToNorm(Point p, Rect rect) => new((p.X - rect.X) / rect.Width, (p.Y - rect.Y) / rect.Height);

    private static Point ToScreen(NormPoint p, Rect rect) => new(rect.X + p.X * rect.Width, rect.Y + p.Y * rect.Height);

    /// <summary>Releases the decoded image.</summary>
    public void Dispose()
    {
        _bitmap?.Dispose();
        _bitmap = null;
        _bitmapSource = null;
        GC.SuppressFinalize(this);
    }

    /// <summary>Decodes the session's image once per image. False when there is no image or it cannot be decoded.</summary>
    private bool EnsureBitmap()
    {
        var source = _session.Source;
        if (ReferenceEquals(source, _bitmapSource)) return _bitmap is not null;

        _bitmap?.Dispose();
        _bitmap = null;
        _bitmapSource = source;
        if (source is null) return false;

        try
        {
            using var input = new MemoryStream(source.Bytes.ToArray(), writable: false);
            _bitmap = new Bitmap(input);
        }
        catch (Exception)
        {
            // An undecodable image shows as an empty canvas; the flatten step reports the render failure on Send.
            _bitmap = null;
        }
        return _bitmap is not null;
    }
}
