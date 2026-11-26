namespace Bazaar.Client.Motion;

/// <summary>
/// Every duration, curve and limit the motion engine uses. Views never carry literals; a theme or motion document may
/// replace this record through the normal scopes (doc 93, "Everything is a Rubedo-overridable token"). Defaults are the doc's
/// initial values, to be tuned on devices. The safety floor (<see cref="ReducedFade"/> at most 90 ms) is applied by <see cref="Clamped"/>.
/// </summary>
public sealed record MotionTokens
{
    /// <summary>Shape time of a message arrival at the liquid level (doc 93: message arrival 320 ms).</summary>
    public TimeSpan MessageArrival { get; init; } = TimeSpan.FromMilliseconds(320);

    /// <summary>Liquid level: text fades in this long after the shape has settled, so it stays sharp.</summary>
    public TimeSpan TextFadeIn { get; init; } = TimeSpan.FromMilliseconds(120);

    /// <summary>Subtle level: scale and fade (new message) or slide and fade (own message), 160 ms.</summary>
    public TimeSpan SubtleDuration { get; init; } = TimeSpan.FromMilliseconds(160);

    /// <summary>Reduced level: one cross-fade, 90 ms.</summary>
    public TimeSpan ReducedFade { get; init; } = TimeSpan.FromMilliseconds(90);

    /// <summary>The floor for <see cref="ReducedFade"/>: no override may exceed it.</summary>
    public static TimeSpan ReducedFadeCeiling { get; } = TimeSpan.FromMilliseconds(90);

    /// <summary>Spring damping ratio of the expand phase; 0.7 gives one overshoot of about 5 percent.</summary>
    public double SpringDampingRatio { get; init; } = 0.7;

    /// <summary>Damping ratio used when an animation is interrupted and settles to rest: critically damped, no new overshoot.</summary>
    public double InterruptDampingRatio { get; init; } = 1.0;

    /// <summary>How long an interrupted animation takes to settle to rest.</summary>
    public TimeSpan InterruptSettle { get; init; } = TimeSpan.FromMilliseconds(160);

    /// <summary>Share of <see cref="MessageArrival"/> the drop spends travelling before it expands.</summary>
    public double TravelFraction { get; init; } = 0.4;

    /// <summary>Drop size at spawn, relative to the final bubble.</summary>
    public double DropletStartScale { get; init; } = 0.12;

    /// <summary>Bend of the drop's path as a share of the travel distance (0 is a straight line).</summary>
    public double DropletArc { get; init; } = 0.12;

    /// <summary>Largest stretch along the direction of travel (volume is kept, so the other axis shrinks to 1 / this).</summary>
    public double MaxStretch { get; init; } = 1.35;

    /// <summary>Smallest scale along an axis when squashing (volume is kept).</summary>
    public double MaxSquash { get; init; } = 0.8;

    /// <summary>Subtle level: starting scale of a new message.</summary>
    public double SubtleStartScale { get; init; } = 0.96;

    /// <summary>Subtle level: slide distance of an own message, in device-independent pixels.</summary>
    public double SubtleSlide { get; init; } = 8;

    /// <summary>Typing indicator: one full cycle of the three dots.</summary>
    public TimeSpan TypingCycle { get; init; } = TimeSpan.FromMilliseconds(1200);

    /// <summary>Subtle level: bounce height of a typing dot.</summary>
    public double TypingBounce { get; init; } = 4;

    /// <summary>Applies the safety floors: reduced fades never exceed 90 ms, and the limits stay sane and volume-keeping.</summary>
    public MotionTokens Clamped() => this with
    {
        ReducedFade = ReducedFade > ReducedFadeCeiling ? ReducedFadeCeiling : ReducedFade,
        SpringDampingRatio = Math.Clamp(SpringDampingRatio, 0.05, 2.0),
        InterruptDampingRatio = Math.Clamp(InterruptDampingRatio, 0.05, 2.0),
        TravelFraction = Math.Clamp(TravelFraction, 0.05, 0.9),
        DropletStartScale = Math.Clamp(DropletStartScale, 0.01, 1.0),
        MaxStretch = Math.Max(MaxStretch, 1.0),
        MaxSquash = Math.Clamp(MaxSquash, 0.1, 1.0),
    };

    /// <summary>The shipped defaults.</summary>
    public static MotionTokens Default { get; } = new();
}
