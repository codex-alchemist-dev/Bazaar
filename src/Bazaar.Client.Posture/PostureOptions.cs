namespace Bazaar.Client.Posture;

/// <summary>Where the classifier and the layout selector draw their lines. Initial values, to be tuned on devices.</summary>
public sealed record PostureThresholds
{
    /// <summary>Fold angle at or above which a half-open device counts as flat.</summary>
    public double FlatEnterDegrees { get; init; } = 165;

    /// <summary>Fold angle below which a flat device counts as half open again (hysteresis, so the posture does not flicker near flat).</summary>
    public double FlatExitDegrees { get; init; } = 155;

    /// <summary>Window width (device-independent pixels) from which a flat window gets the dual-pane layout.</summary>
    public double DualPaneMinWidth { get; init; } = 600;

    /// <summary>Share of the window the first pane (chat) takes when there is no hinge to split at.</summary>
    public double SplitRatio { get; init; } = 0.4;

    /// <summary>Applies sane limits.</summary>
    public PostureThresholds Clamped()
    {
        var enter = Math.Clamp(FlatEnterDegrees, 90, 180);
        return this with
        {
            FlatEnterDegrees = enter,
            FlatExitDegrees = Math.Clamp(FlatExitDegrees, 45, enter),
            DualPaneMinWidth = Math.Max(DualPaneMinWidth, 0),
            SplitRatio = Math.Clamp(SplitRatio, 0.2, 0.8),
        };
    }

    public static PostureThresholds Default { get; } = new();
}

/// <summary>Durations and limits of the unfold transition. Views carry no literals; a motion document may replace this record (doc 93, tokens).</summary>
public sealed record PostureMotionTokens
{
    /// <summary>Time to settle at the liquid level; the doc 93 sheet token, 420 ms. The subtle level uses <see cref="SubtleScale"/> of it.</summary>
    public TimeSpan UnfoldDuration { get; init; } = TimeSpan.FromMilliseconds(420);

    /// <summary>Subtle level: share of the duration used (about half, no overshoot).</summary>
    public double SubtleScale { get; init; } = 0.5;

    /// <summary>Fold angle that counts as closed (progress 0).</summary>
    public double ClosedDegrees { get; init; } = 20;

    /// <summary>Fold angle that counts as fully open (progress 1).</summary>
    public double OpenDegrees { get; init; } = 170;

    /// <summary>While tracking the hinge: no new angle for this long means the hands let go, and the layout settles by spring.</summary>
    public TimeSpan TrackIdle { get; init; } = TimeSpan.FromMilliseconds(120);

    /// <summary>How far the new pane's slide starts from the hinge, as a share of the pane's extent.</summary>
    public double SlideFraction { get; init; } = 0.3;

    /// <summary>Applies sane limits.</summary>
    public PostureMotionTokens Clamped() => this with
    {
        UnfoldDuration = UnfoldDuration < TimeSpan.FromMilliseconds(1) ? TimeSpan.FromMilliseconds(1) : UnfoldDuration,
        SubtleScale = Math.Clamp(SubtleScale, 0.1, 1.0),
        ClosedDegrees = Math.Clamp(ClosedDegrees, 0, 90),
        OpenDegrees = Math.Clamp(OpenDegrees, Math.Max(ClosedDegrees + 1, 91), 180),
        SlideFraction = Math.Clamp(SlideFraction, 0, 1),
    };

    public static PostureMotionTokens Default { get; } = new();
}
