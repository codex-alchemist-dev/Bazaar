namespace Bazaar.Client.Mobile;

/// <summary>
/// The phone gesture thresholds (doc 93, Phone gestures and Themes). Every value is a setting, so a company or a person can
/// change it; the edge inset has a floor so the system back gesture always wins at the screen edge.
/// </summary>
public sealed record GestureOptions
{
    /// <summary>The lowest edge inset a setting may ask for, in dp (doc 93 setting <c>android.gesture_edge_dp</c>).</summary>
    public const double EdgeInsetFloorDp = 16;

    private readonly double _edgeInsetDp = 24;

    /// <summary>How long a press must stay put to open the radial menu (doc 93: 400 ms).</summary>
    public TimeSpan HoldTime { get; init; } = TimeSpan.FromMilliseconds(400);

    /// <summary>
    /// Touches that start closer than this to a vertical screen edge are not ours. Values below
    /// <see cref="EdgeInsetFloorDp"/> are raised to it.
    /// </summary>
    public double EdgeInsetDp
    {
        get => _edgeInsetDp;
        init => _edgeInsetDp = Math.Max(value, EdgeInsetFloorDp);
    }

    /// <summary>How far a touch may wander before it stops being a press and becomes a swipe or a scroll, in dp.</summary>
    public double SlopDp { get; init; } = 8;

    /// <summary>The horizontal distance that commits a swipe on release, in dp.</summary>
    public double SwipeCommitDp { get; init; } = 72;

    /// <summary>A flick faster than this commits a swipe on release even when it is short, in dp per second.</summary>
    public double SwipeCommitVelocityDpPerSecond { get; init; } = 600;
}
