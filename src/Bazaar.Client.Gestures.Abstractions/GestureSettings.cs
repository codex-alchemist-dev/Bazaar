namespace Bazaar.Client.Gestures;

/// <summary>
/// The thresholds of predictive back and of the spring settle after a swipe (doc 93). Views never carry literals; a theme or motion
/// document may replace this record through the normal scopes (doc 93, "Everything is a Rubedo-overridable token"). Swipe, hold and
/// edge thresholds are not here: they live in <c>GestureOptions</c> in Bazaar.Client.Mobile. <see cref="Clamped"/> keeps every value usable.
/// </summary>
public sealed record GestureSettings
{
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

    /// <summary>Applies the limits.</summary>
    public GestureSettings Clamped() => this with
    {
        BackMinScale = Math.Clamp(BackMinScale, 0.5, 1),
        BackMaxShift = Math.Clamp(BackMaxShift, 0, 0.3),
        BackBehindMinScale = Math.Clamp(BackBehindMinScale, 0.5, 1),
        SettleTime = SettleTime < TimeSpan.FromMilliseconds(30) ? TimeSpan.FromMilliseconds(30) : SettleTime,
        SettleDamping = Math.Clamp(SettleDamping, 0.3, 2.0),
    };

    /// <summary>The shipped defaults.</summary>
    public static GestureSettings Default { get; } = new();
}
