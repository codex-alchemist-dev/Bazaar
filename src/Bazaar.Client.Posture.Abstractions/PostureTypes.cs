namespace Bazaar.Client.Posture;

/// <summary>
/// How a foldable's inner screen is held (doc 93, "Foldable unfold transition and postures"). The folded cover screen is not a
/// posture: the window is then compact and has no hinge, which the layout selector sees as <see cref="PostureLayoutClass.Folded"/>.
/// </summary>
public enum DevicePosture
{
    /// <summary>Fully open and flat (or not a foldable at all).</summary>
    Flat,

    /// <summary>Half open with a vertical hinge: the two halves are two pages.</summary>
    Book,

    /// <summary>Half open with a horizontal hinge: content above, controls below.</summary>
    Tabletop,

    /// <summary>Half open and the hinge orientation is unknown.</summary>
    HalfOpen,
}

/// <summary>Direction of the hinge line.</summary>
public enum HingeOrientation
{
    /// <summary>No hinge reported.</summary>
    None,

    /// <summary>The hinge runs top to bottom; the screen splits left and right.</summary>
    Vertical,

    /// <summary>The hinge runs left to right; the screen splits top and bottom.</summary>
    Horizontal,
}

/// <summary>The named layouts of doc 93, plus the folded single-pane layout every other layout falls back to.</summary>
public enum PostureLayoutClass
{
    /// <summary>Single pane (cover screen, phone, or no variant for the posture).</summary>
    Folded,

    /// <summary>Flat and fully open: the doc 29 unfolded layout, chat beside the pane pager.</summary>
    DualPane,

    /// <summary>Half open, vertical hinge: chat left, current pane right, hinge gap kept clear.</summary>
    Book,

    /// <summary>Half open, horizontal hinge: content on top, controls (composer, approval card, radial hit area) below.</summary>
    Tabletop,
}

/// <summary>Which server-sent layout variant the client chose for the current posture (doc 93: picked locally, no round trip).</summary>
public enum LayoutVariantKind
{
    Folded,
    Unfolded,
    Book,
    Tabletop,

    /// <summary>The single half-open variant (<c>android.half_open_layout</c>) used when no book or tabletop variant exists.</summary>
    HalfOpen,
}

/// <summary>
/// The hinge as an inset: it occupies <see cref="Size"/> device-independent pixels starting <see cref="Offset"/> from the window's
/// leading edge (left for a vertical hinge, top for a horizontal one). Content never straddles it; it is a layout gap, not a drawn surface.
/// </summary>
public readonly record struct HingeInset(HingeOrientation Orientation, double Offset, double Size)
{
    /// <summary>No hinge.</summary>
    public static HingeInset None { get; } = new(HingeOrientation.None, 0, 0);

    /// <summary>True when there is no hinge.</summary>
    public bool IsNone => Orientation == HingeOrientation.None;
}

/// <summary>
/// One reading from a posture source. <see cref="FoldAngleDegrees"/> is null where the platform does not report the angle
/// (0 is closed, 180 is flat); then the transition plays on the posture change instead of following the hinge.
/// </summary>
public sealed record PostureState(DevicePosture Posture, double? FoldAngleDegrees, HingeInset Hinge)
{
    /// <summary>A flat device without a hinge: every non-foldable device.</summary>
    public static PostureState Flat { get; } = new(DevicePosture.Flat, null, HingeInset.None);
}

/// <summary>
/// Where the client learns the posture. The Android head implements it over WindowManager (<c>WindowLayoutInfo</c> and, where
/// the bindings expose it, the fold angle; unverified, doc 35 spike); the rest of the client sees only this interface.
/// </summary>
public interface IPostureSource
{
    /// <summary>The latest reading.</summary>
    PostureState Current { get; }

    /// <summary>Raised when the posture, the angle or the hinge changes. May be raised on any thread; consumers marshal.</summary>
    event EventHandler<PostureState>? Changed;
}
