using Avalonia;
using Avalonia.Controls;

namespace Bazaar.Client.Posture.Avalonia;

/// <summary>
/// Arranges its first two children by the posture: the first (chat) and the second (current pane) side by side or, in tabletop,
/// stacked, with the hinge gap left empty. <see cref="TransitionProgress"/> moves the layout between folded (first child fills
/// the window) and the settled class. Layout only; opacity and slide of the second child belong to the driver. Further children are ignored.
/// </summary>
public sealed class PosturePanel : Panel
{
    /// <summary>0 is folded, 1 is settled in the layout class; a liquid spring may pass 1.</summary>
    public static readonly StyledProperty<double> TransitionProgressProperty =
        AvaloniaProperty.Register<PosturePanel, double>(nameof(TransitionProgress), 0.0);

    /// <summary>Share of the window the first pane takes where there is no hinge to split at.</summary>
    public static readonly StyledProperty<double> SplitRatioProperty =
        AvaloniaProperty.Register<PosturePanel, double>(nameof(SplitRatio), 0.4);

    static PosturePanel()
    {
        AffectsMeasure<PosturePanel>(TransitionProgressProperty, SplitRatioProperty);
        AffectsArrange<PosturePanel>(TransitionProgressProperty, SplitRatioProperty);
        PostureLayout.HingeInsetProperty.Changed.AddClassHandler<PosturePanel>((p, _) => p.InvalidateMeasure());
        PostureLayout.LayoutClassProperty.Changed.AddClassHandler<PosturePanel>((p, _) => p.InvalidateMeasure());
    }

    public double TransitionProgress
    {
        get => GetValue(TransitionProgressProperty);
        set => SetValue(TransitionProgressProperty, value);
    }

    public double SplitRatio
    {
        get => GetValue(SplitRatioProperty);
        set => SetValue(SplitRatioProperty, value);
    }

    /// <summary>The regions the two panes occupy right now for a window of this size.</summary>
    public (PaneRegion First, PaneRegion Second) CurrentRegions(double width, double height) =>
        PostureGeometry.Transitional(PostureLayout.GetLayoutClass(this), PostureLayout.GetHingeInset(this), width, height, TransitionProgress, SplitRatio);

    protected override Size MeasureOverride(Size availableSize)
    {
        var width = double.IsInfinity(availableSize.Width) ? 0 : availableSize.Width;
        var height = double.IsInfinity(availableSize.Height) ? 0 : availableSize.Height;
        var (first, second) = CurrentRegions(width, height);
        MeasureChild(Children.Count > 0 ? Children[0] : null, first);
        MeasureChild(Children.Count > 1 ? Children[1] : null, second);
        return new Size(width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var (first, second) = CurrentRegions(finalSize.Width, finalSize.Height);
        ArrangeChild(Children.Count > 0 ? Children[0] : null, first);
        ArrangeChild(Children.Count > 1 ? Children[1] : null, second);
        for (var i = 2; i < Children.Count; i++)
        {
            Children[i].Arrange(new Rect(0, 0, 0, 0));
        }

        return finalSize;
    }

    private static void MeasureChild(Control? child, PaneRegion region) => child?.Measure(new Size(region.Width, region.Height));

    private static void ArrangeChild(Control? child, PaneRegion region) => child?.Arrange(new Rect(region.X, region.Y, region.Width, region.Height));
}
