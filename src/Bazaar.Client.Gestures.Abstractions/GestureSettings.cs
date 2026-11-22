namespace Bazaar.Client.Gestures;

/// <summary>Setting keys of the gesture module (doc 93, settings table).</summary>
public static class GestureSettingKeys
{
    /// <summary>Int dp; install scope; default 24, floor 16. Swipe recognition ignores this outer strip so the system back gesture wins.</summary>
    public const string EdgeDp = "android.gesture_edge_dp";
}

/// <summary>
/// Every threshold the gesture layer uses. Views never carry literals; a theme or motion document may replace this record through the
/// normal scopes (doc 93, "Everything is a Rubedo-overridable token"). <see cref="Clamped"/> applies the safety floor: the edge exclusion
/// can be widened but never narrowed below <see cref="EdgeFloorDp"/>. Distances are device-independent pixels.
/// </summary>
public sealed record GestureSettings
{
    /// <summary>The floor of <see cref="EdgeDp"/>.</summary>
    public const double EdgeFloorDp = 16;

    /// <summary>Outer strip, on each side, where swipes are not recognised (<c>android.gesture_edge_dp</c>).</summary>
    public double EdgeDp { get; init; } = 24;

    /// <summary>How long a still press must last to open the radial (doc 93: 400 ms).</summary>
    public TimeSpan HoldDuration { get; init; } = TimeSpan.FromMilliseconds(400);

    /// <summary>How far a press may move and still count as a hold, and how far it must move to count as a swipe or scroll.</summary>
    public double TouchSlopDp { get; init; } = 10;

    /// <summary>A movement is horizontal (a swipe) when its horizontal part is at least this times the vertical part; otherwise it is a scroll.</summary>
    public double HorizontalBias { get; init; } = 1.5;

    /// <summary>The pager commits to the neighbour when dragged past this share of the page width.</summary>
    public double PagerCommitFraction { get; init; } = 0.33;

    /// <summary>A release faster than this (dp per second) flings to the neighbour whatever the distance.</summary>
    public double FlingVelocity { get; init; } = 700;

    /// <summary>How many dp of the neighbour page show at the edge while the pager rests, so the gesture is visible.</summary>
    public double PeekDp { get; init; } = 12;

    /// <summary>Resistance when dragging past the first or last page: the page moves this share of the finger.</summary>
    public double OverscrollResistance { get; init; } = 0.3;

    /// <summary>Row swipe: distance at which the action commits (with a haptic tick).</summary>
    public double RowCommitDp { get; init; } = 96;

    /// <summary>Sheet: the half-height snap as a share of the full height.</summary>
    public double SheetHalfFraction { get; init; } = 0.5;

    /// <summary>Predictive back: scale of the current screen at progress 1.</summary>
    public double BackMinScale { get; init; } = 0.9;

    /// <summary>Predictive back: shift of the current screen at progress 1, as a share of the screen width, away from the swipe edge.</summary>
    public double BackMaxShift { get; init; } = 0.06;

    /// <summary>Predictive back: scale of the screen beneath at progress 0 (it grows to 1 as the swipe commits).</summary>
    public double BackBehindMinScale { get; init; } = 0.94;

    /// <summary>Time a released gesture takes to settle to its target with the Fluid spring.</summary>
    public TimeSpan SettleTime { get; init; } = TimeSpan.FromMilliseconds(320);

    /// <summary>Damping ratio of the settle spring; below 1 gives one small overshoot.</summary>
    public double SettleDamping { get; init; } = 0.8;

    /// <summary>Applies the safety floors and keeps every value usable.</summary>
    public GestureSettings Clamped() => this with
    {
        EdgeDp = Math.Max(EdgeDp, EdgeFloorDp),
        HoldDuration = HoldDuration < TimeSpan.FromMilliseconds(50) ? TimeSpan.FromMilliseconds(50) : HoldDuration,
        TouchSlopDp = Math.Max(TouchSlopDp, 1),
        HorizontalBias = Math.Max(HorizontalBias, 1),
        PagerCommitFraction = Math.Clamp(PagerCommitFraction, 0.05, 0.95),
        FlingVelocity = Math.Max(FlingVelocity, 1),
        PeekDp = Math.Max(PeekDp, 0),
        OverscrollResistance = Math.Clamp(OverscrollResistance, 0, 1),
        RowCommitDp = Math.Max(RowCommitDp, 8),
        SheetHalfFraction = Math.Clamp(SheetHalfFraction, 0.1, 0.9),
        BackMinScale = Math.Clamp(BackMinScale, 0.5, 1),
        BackMaxShift = Math.Clamp(BackMaxShift, 0, 0.3),
        BackBehindMinScale = Math.Clamp(BackBehindMinScale, 0.5, 1),
        SettleTime = SettleTime < TimeSpan.FromMilliseconds(30) ? TimeSpan.FromMilliseconds(30) : SettleTime,
        SettleDamping = Math.Clamp(SettleDamping, 0.3, 2.0),
    };

    /// <summary>The shipped defaults.</summary>
    public static GestureSettings Default { get; } = new();
}
