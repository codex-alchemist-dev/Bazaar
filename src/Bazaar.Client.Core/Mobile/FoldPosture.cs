namespace Bazaar.Client.Core.Mobile;

/// <summary>Fold state as the platform reports it (FoldingFeature.State on Android). Provisional, pending spike S13 (doc 29).</summary>
public enum FoldState
{
    Flat,
    HalfOpened,
}

/// <summary>
/// Maps Jetpack WindowManager fold data to <see cref="PostureKind"/>. Pure: the Android head reads the
/// platform values and passes them in (doc 29, posture source). Provisional, pending spike S13.
/// </summary>
public static class FoldPosture
{
    /// <remarks>
    /// Rules, in order:
    /// 1. No folding feature: <see cref="PostureKind.Folded"/>. PostureKind has no flat or non-foldable value;
    ///    Folded is the nearest existing one and selects the phone layout.
    /// 2. A separating feature: <see cref="PostureKind.Folded"/>, whatever the fold state.
    /// 3. Flat: <see cref="PostureKind.Unfolded"/>.
    /// 4. Half-opened: <see cref="PostureKind.HalfOpen"/>. PostureKind has no unfolded-with-hinge value, so a
    ///    vertical hinge also maps to HalfOpen, so <paramref name="hingeIsHorizontal"/> does not change the result.
    /// </remarks>
    public static PostureKind Map(bool hasFoldingFeature, FoldState state, bool hingeIsHorizontal, bool hingeIsSeparating)
    {
        if (!hasFoldingFeature)
        {
            return PostureKind.Folded;
        }

        if (hingeIsSeparating)
        {
            return PostureKind.Folded;
        }

        return state switch
        {
            FoldState.Flat => PostureKind.Unfolded,
            FoldState.HalfOpened => PostureKind.HalfOpen,
            _ => PostureKind.Folded,
        };
    }
}
