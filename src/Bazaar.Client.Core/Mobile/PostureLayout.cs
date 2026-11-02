namespace Bazaar.Client.Core.Mobile;

/// <summary>Physical posture of a foldable as the platform reports it (WindowLayoutInfo on Android).</summary>
public enum PostureKind
{
    Folded,
    HalfOpen,
    Unfolded,
}

/// <summary>Which server-sent layout variant the phone shows. The phone picks one; it never computes layout (doc 29, C27).</summary>
public enum LayoutVariant
{
    Folded,
    Unfolded,
}

/// <summary>Setting <c>android.half_open_layout</c>.</summary>
public enum HalfOpenLayoutPolicy
{
    UnfoldedIfWide,
    Folded,
}

public static class PostureLayout
{
    /// <summary>Narrowest width, in device-independent pixels, that can show the unfolded layout.</summary>
    public const double MinUnfoldedWidthDp = 600;

    public static LayoutVariant Select(PostureKind posture, HalfOpenLayoutPolicy halfOpen, double widthDp) =>
        posture switch
        {
            PostureKind.Unfolded => LayoutVariant.Unfolded,
            PostureKind.HalfOpen when halfOpen == HalfOpenLayoutPolicy.UnfoldedIfWide && widthDp >= MinUnfoldedWidthDp
                => LayoutVariant.Unfolded,
            _ => LayoutVariant.Folded,
        };
}
