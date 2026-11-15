namespace Bazaar.Server.Agents.Turns;

/// <summary>
/// Limits for one turn (doc 06, "Turn loop" and "Settings"). <see cref="StepCap"/> is the settings key
/// <c>runtime.turn_step_cap</c>; the token ceiling and wall clock are the turn's other stop conditions. Every limit is
/// checked by <see cref="TurnRunner"/>; none can be loosened by a model response.
/// </summary>
public sealed class TurnRunnerOptions
{
    /// <summary>Most steps one turn may take, model and tool steps alike, including reused ones (<c>runtime.turn_step_cap</c>).</summary>
    public int StepCap { get; init; } = 20;

    /// <summary>Most tokens (input, cache, output) one turn may spend on new model calls. Reused steps spend nothing.</summary>
    public long TokenCeiling { get; init; } = 200_000;

    /// <summary>Longest time a turn may run before it is failed. A running model call is cancelled when this passes.</summary>
    public TimeSpan WallClock { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>How long the agent lease lasts. It must outlive <see cref="WallClock"/>, since nothing renews it in this run.</summary>
    public TimeSpan LeaseDuration { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Tool names whose calls may run again after a crash (doc 06, "Concurrency and recovery"). Every other tool is treated as
    /// non-idempotent: an in-flight call is recorded as unknown and the model is told. The default names none.
    /// </summary>
    public IReadOnlyCollection<string> IdempotentTools { get; init; } = [];

    /// <summary>Checks the limits. Throws on the first problem.</summary>
    /// <exception cref="ArgumentOutOfRangeException">A limit is out of range.</exception>
    /// <exception cref="ArgumentException">The lease does not outlive the wall clock, or a tool name is blank.</exception>
    public void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(StepCap, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(TokenCeiling, 1L);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(WallClock, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(LeaseDuration, TimeSpan.Zero);
        if (LeaseDuration <= WallClock)
            throw new ArgumentException("The lease must last longer than the wall clock.", nameof(LeaseDuration));
        ArgumentNullException.ThrowIfNull(IdempotentTools);
        foreach (var name in IdempotentTools)
            ArgumentException.ThrowIfNullOrWhiteSpace(name);
    }
}
