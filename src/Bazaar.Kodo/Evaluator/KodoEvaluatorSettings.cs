namespace Bazaar.Kodo.Evaluator;

/// <summary>
/// The evaluator's settings (doc 46 section 20). Values are operator settings with safe defaults and
/// may tighten the floors, never loosen them.
/// </summary>
public sealed record KodoEvaluatorSettings
{
    /// <summary>Stability window before a recovery or degraded state may become healthy (<c>kodo.state.stability_window_s</c>, default 5 minutes, floor 60 seconds).</summary>
    public TimeSpan StabilityWindow { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>Transitions allowed in <see cref="FlapWindow"/> before the state is flapping (<c>kodo.state.flap_limit</c>, default 6 in 30 minutes; more than this flaps).</summary>
    public int FlapLimit { get; init; } = 6;

    /// <summary>The window over which transitions are counted for flap detection (section 8.2).</summary>
    public TimeSpan FlapWindow { get; init; } = TimeSpan.FromMinutes(30);

    /// <summary>The floor for <see cref="StabilityWindow"/> (section 20).</summary>
    public static TimeSpan StabilityWindowFloor { get; } = TimeSpan.FromSeconds(60);

    /// <summary>Throws if a value is outside its floor or range.</summary>
    public void Validate()
    {
        if (StabilityWindow < StabilityWindowFloor)
        {
            throw new ArgumentOutOfRangeException(nameof(StabilityWindow), StabilityWindow, "The stability window cannot be below its 60 second floor (section 20).");
        }

        if (FlapLimit < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(FlapLimit), FlapLimit, "The flap limit must be at least 1.");
        }

        if (FlapWindow <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(FlapWindow), FlapWindow, "The flap window must be positive.");
        }
    }
}
