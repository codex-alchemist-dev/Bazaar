namespace Bazaar.Client.Layout;

/// <summary>
/// Chooses the posture variant of a foldable from its hinge posture and window width (doc 41 section 4.8, doc 41a
/// section 3). Pure: no platform types, so every head runs the same rule and tests cover it without a device.
/// </summary>
/// <remarks>
/// Fail closed: an unknown posture or a width that is not a positive finite number selects no variant. Then
/// <see cref="Place"/> ignores the company's effective layout and shows the baked default in its folded variant,
/// the narrowest and most conservative arrangement, so an unvetted or unexpected value never widens the screen.
/// </remarks>
public static class FoldableLayoutSelection
{
    /// <summary>Narrowest window width, in dp, that shows the unfolded variant while the device is half-open (owner, 2026-11-12).</summary>
    public const double HalfOpenMinWidthDp = 600;

    /// <summary>Posture key of the folded (cover) variant.</summary>
    public const string FoldedPosture = "folded";

    /// <summary>Posture key of the unfolded (inner screen) variant.</summary>
    public const string UnfoldedPosture = "unfolded";

    /// <summary>Hinge posture key sent by the server for a half-open device.</summary>
    public const string HalfOpenPosture = "halfOpen";

    /// <summary>
    /// Maps a hinge posture and window width to the posture key used to look up slots. Folded and unfolded map to
    /// themselves. Half-open maps to unfolded at or above <see cref="HalfOpenMinWidthDp"/> and to folded below it.
    /// Returns null (fail closed) for an unknown posture, a null posture, or a width that is not a positive finite number.
    /// </summary>
    public static string? VariantFor(string? posture, double widthDp)
    {
        if (!double.IsFinite(widthDp) || widthDp <= 0)
        {
            return null;
        }

        return posture switch
        {
            FoldedPosture => FoldedPosture,
            UnfoldedPosture => UnfoldedPosture,
            HalfOpenPosture => widthDp >= HalfOpenMinWidthDp ? UnfoldedPosture : FoldedPosture,
            _ => null,
        };
    }

    /// <summary>
    /// Places the layout for a foldable's current posture and width. When <see cref="VariantFor"/> yields a variant, the
    /// effective layout and then the baked default are resolved for it, as in <see cref="LayoutSelection.Place"/>. When
    /// it yields none, only the baked default is used, in its folded variant.
    /// </summary>
    /// <returns>The placed tree, or null when no matching slot exists.</returns>
    public static PlacedNode? Place(
        LayoutDocument? effective,
        LayoutDocument? bakedDefault,
        string deviceClass,
        string display,
        string? posture,
        double widthDp,
        Func<string, bool> isKnownPaneKind)
    {
        string? variant = VariantFor(posture, widthDp);
        if (variant is null)
        {
            return LayoutSelection.Place(null, bakedDefault, deviceClass, display, FoldedPosture, isKnownPaneKind);
        }

        return LayoutSelection.Place(effective, bakedDefault, deviceClass, display, variant, isKnownPaneKind);
    }
}
