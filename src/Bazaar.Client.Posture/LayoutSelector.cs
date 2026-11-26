namespace Bazaar.Client.Posture;

/// <summary>The layout variants the server sent for this company (doc 93: the client picks among them locally).</summary>
[Flags]
public enum LayoutVariants
{
    None = 0,
    Unfolded = 1,
    Book = 2,
    Tabletop = 4,
    HalfOpen = 8,
    All = Unfolded | Book | Tabletop | HalfOpen,
}

/// <summary>The chosen layout class and the variant it came from.</summary>
public readonly record struct LayoutSelection(PostureLayoutClass Class, LayoutVariantKind Variant);

/// <summary>Picks a layout class from the posture, the window and the variants the company has. Pure, no round trip.</summary>
public static class LayoutSelector
{
    /// <summary>
    /// Flat and wide enough gives dual-pane. Half open gives book or tabletop; with no variant for that, the half-open variant
    /// (<c>android.half_open_layout</c>, same geometry by hinge orientation), then folded. Anything else is folded.
    /// </summary>
    public static LayoutSelection Select(PostureState state, double windowWidth, LayoutVariants available, PostureThresholds? thresholds = null)
    {
        var t = (thresholds ?? PostureThresholds.Default).Clamped();
        var folded = new LayoutSelection(PostureLayoutClass.Folded, LayoutVariantKind.Folded);

        switch (state.Posture)
        {
            case DevicePosture.Flat:
                return windowWidth >= t.DualPaneMinWidth && available.HasFlag(LayoutVariants.Unfolded)
                    ? new LayoutSelection(PostureLayoutClass.DualPane, LayoutVariantKind.Unfolded)
                    : folded;

            case DevicePosture.Book:
                return Half(available, LayoutVariants.Book, PostureLayoutClass.Book, LayoutVariantKind.Book, folded);

            case DevicePosture.Tabletop:
                return Half(available, LayoutVariants.Tabletop, PostureLayoutClass.Tabletop, LayoutVariantKind.Tabletop, folded);

            default:
                if (!available.HasFlag(LayoutVariants.HalfOpen))
                {
                    return folded;
                }

                var cls = state.Hinge.Orientation == HingeOrientation.Horizontal ? PostureLayoutClass.Tabletop : PostureLayoutClass.Book;
                return new LayoutSelection(cls, LayoutVariantKind.HalfOpen);
        }
    }

    private static LayoutSelection Half(LayoutVariants available, LayoutVariants exact, PostureLayoutClass cls, LayoutVariantKind kind, LayoutSelection folded)
    {
        if (available.HasFlag(exact))
        {
            return new LayoutSelection(cls, kind);
        }

        return available.HasFlag(LayoutVariants.HalfOpen) ? new LayoutSelection(cls, LayoutVariantKind.HalfOpen) : folded;
    }
}
