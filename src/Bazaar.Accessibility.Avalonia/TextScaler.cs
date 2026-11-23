using Avalonia.Controls;
using Avalonia.Media;

namespace Bazaar.Accessibility.Avalonia;

/// <summary>
/// Scales the content it hosts (doc 49 section 2: text can be scaled to at least 200 percent without loss of function).
/// The whole content is scaled through a layout transform, so what the content measures and arranges follows the scale
/// and nothing overlaps; a scrollable pane then scrolls instead of clipping.
/// </summary>
public sealed class TextScaler
{
    /// <summary>Smallest scale: no scaling.</summary>
    public const double Minimum = 1.0;

    /// <summary>Largest scale. Doc 49 asks for at least 200 percent; 300 leaves room above that floor.</summary>
    public const double Maximum = 3.0;

    private readonly ScaleTransform _transform = new(1, 1);

    public TextScaler() =>
        Host = new LayoutTransformControl { LayoutTransform = _transform };

    /// <summary>The control to place in the window; set its child with <see cref="SetContent"/>.</summary>
    public LayoutTransformControl Host { get; }

    /// <summary>The current scale, 1.0 for normal size, clamped to <see cref="Minimum"/>..<see cref="Maximum"/>.</summary>
    public double Scale { get; private set; } = 1.0;

    public void SetContent(Control? content) => Host.Child = content;

    /// <summary>Sets the scale. Values outside the range, and non-numbers, are clamped to the nearest valid value (1.0 for NaN).</summary>
    public void SetScale(double scale)
    {
        Scale = double.IsNaN(scale) ? Minimum : Math.Clamp(scale, Minimum, Maximum);
        _transform.ScaleX = Scale;
        _transform.ScaleY = Scale;
    }
}
