namespace Bazaar.Client.Posture;

/// <summary>A rectangle in window coordinates, in device-independent pixels.</summary>
public readonly record struct PaneRegion(double X, double Y, double Width, double Height)
{
    public bool IsEmpty => Width <= 0 || Height <= 0;

    /// <summary>Linear blend; <paramref name="t"/> is not clamped, so a spring overshoot carries through.</summary>
    public static PaneRegion Lerp(PaneRegion a, PaneRegion b, double t) => new(
        a.X + (b.X - a.X) * t,
        a.Y + (b.Y - a.Y) * t,
        Math.Max(0, a.Width + (b.Width - a.Width) * t),
        Math.Max(0, a.Height + (b.Height - a.Height) * t));
}

/// <summary>Where the two panes of a layout class sit, keeping the hinge gap clear (doc 93: content never straddles the hinge).</summary>
public static class PostureGeometry
{
    /// <summary>
    /// The settled regions of the first pane (chat; content in tabletop) and the second (current pane; controls in tabletop).
    /// Folded gives the first pane the whole window and the second nothing. Without a usable hinge the split is at <paramref name="splitRatio"/> and the gap is zero.
    /// </summary>
    public static (PaneRegion First, PaneRegion Second) Settled(PostureLayoutClass layout, HingeInset hinge, double width, double height, double splitRatio = 0.4)
    {
        var full = new PaneRegion(0, 0, width, height);
        if (layout == PostureLayoutClass.Folded)
        {
            return (full, new PaneRegion(width, 0, 0, height));
        }

        var horizontal = layout == PostureLayoutClass.Tabletop;
        var extent = horizontal ? height : width;
        double start;
        double gap;
        if (hinge.IsNone || (hinge.Orientation == HingeOrientation.Horizontal) != horizontal)
        {
            start = extent * splitRatio;
            gap = 0;
        }
        else
        {
            start = Math.Clamp(hinge.Offset, 0, extent);
            gap = Math.Clamp(hinge.Size, 0, extent - start);
        }

        var rest = Math.Max(0, extent - start - gap);
        return horizontal
            ? (new PaneRegion(0, 0, width, start), new PaneRegion(0, start + gap, width, rest))
            : (new PaneRegion(0, 0, start, height), new PaneRegion(start + gap, 0, rest, height));
    }

    /// <summary>
    /// The regions part-way through an unfold: the first pane grows from the whole window into its settled region, the second
    /// opens from nothing at the far edge. <paramref name="progress"/> 0 is folded, 1 is settled.
    /// </summary>
    public static (PaneRegion First, PaneRegion Second) Transitional(PostureLayoutClass layout, HingeInset hinge, double width, double height, double progress, double splitRatio = 0.4)
    {
        var (first, second) = Settled(layout, hinge, width, height, splitRatio);
        var foldedFirst = new PaneRegion(0, 0, width, height);
        var foldedSecond = layout == PostureLayoutClass.Tabletop ? new PaneRegion(0, height, width, 0) : new PaneRegion(width, 0, 0, height);
        return (PaneRegion.Lerp(foldedFirst, first, progress), PaneRegion.Lerp(foldedSecond, second, progress));
    }
}
