namespace Bazaar.Client.Annotation;

/// <summary>
/// A point on the captured image, in fractions of its width and height (0 to 1). Coordinates are normalized so the
/// on-screen view and the flattened image agree whatever the display size is.
/// </summary>
public readonly record struct NormPoint(double X, double Y)
{
    /// <summary>The point with both coordinates clamped to 0 to 1.</summary>
    public NormPoint Clamped() => new(Math.Clamp(X, 0, 1), Math.Clamp(Y, 0, 1));
}

/// <summary>One mark the user added. Items keep the order they were drawn in, so undo removes the last one.</summary>
public abstract record AnnotationItem;

/// <summary>A freehand stroke. <see cref="WidthFraction"/> is the pen width as a fraction of the image's shorter side.</summary>
/// <param name="Points">Two or more clamped points, in drawing order.</param>
/// <param name="Argb">Colour as 0xAARRGGBB.</param>
/// <param name="WidthFraction">Pen width, as a fraction of the shorter side of the image.</param>
public sealed record StrokeItem(IReadOnlyList<NormPoint> Points, uint Argb, double WidthFraction) : AnnotationItem;

/// <summary>A text note. <see cref="SizeFraction"/> is the em size as a fraction of the image's shorter side.</summary>
/// <param name="At">Top-left corner of the text.</param>
/// <param name="Text">Trimmed, non-empty text.</param>
/// <param name="Argb">Colour as 0xAARRGGBB.</param>
/// <param name="SizeFraction">Em size, as a fraction of the shorter side of the image.</param>
public sealed record TextItem(NormPoint At, string Text, uint Argb, double SizeFraction) : AnnotationItem;

/// <summary>
/// The annotations on one screenshot. It holds no image bytes and no network state: a model the view edits and
/// the flattener draws. Every add is checked against <see cref="AnnotationOptions"/> and refused, not truncated.
/// </summary>
public sealed class AnnotationDocument
{
    private readonly List<AnnotationItem> _items = [];
    private readonly AnnotationOptions _options;

    /// <param name="options">The limits and default ink, validated by the caller.</param>
    public AnnotationDocument(AnnotationOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
    }

    /// <summary>The items in drawing order.</summary>
    public IReadOnlyList<AnnotationItem> Items => _items;

    /// <summary>Number of items.</summary>
    public int Count => _items.Count;

    /// <summary>
    /// Adds a stroke with the default ink and pen width. Refused (false) when it has fewer than two points, more than
    /// <see cref="AnnotationOptions.MaxPointsPerStroke"/>, or the document already holds <see cref="AnnotationOptions.MaxItems"/>.
    /// </summary>
    public bool TryAddStroke(IReadOnlyList<NormPoint> points)
    {
        ArgumentNullException.ThrowIfNull(points);
        if (points.Count < 2 || points.Count > _options.MaxPointsPerStroke || _items.Count >= _options.MaxItems) return false;

        var clamped = points.Select(p => p.Clamped()).ToArray();
        _items.Add(new StrokeItem(clamped, _options.InkArgb, _options.StrokeWidthFraction));
        return true;
    }

    /// <summary>
    /// Adds a text note with the default ink and size. The text is trimmed; refused (false) when it is empty, longer than
    /// <see cref="AnnotationOptions.MaxTextLength"/>, or the document is full.
    /// </summary>
    public bool TryAddText(NormPoint at, string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var trimmed = text.Trim();
        if (trimmed.Length == 0 || trimmed.Length > _options.MaxTextLength || _items.Count >= _options.MaxItems) return false;

        _items.Add(new TextItem(at.Clamped(), trimmed, _options.InkArgb, _options.TextSizeFraction));
        return true;
    }

    /// <summary>Removes the last item. False when there is nothing to undo.</summary>
    public bool Undo()
    {
        if (_items.Count == 0) return false;
        _items.RemoveAt(_items.Count - 1);
        return true;
    }
}
