using Bazaar.Contracts.Gateway;

namespace Bazaar.Server.Agents.Turns.Runner;

/// <summary>
/// Limits and tool settings for <see cref="TurnRunner"/> (doc 06 "Turn loop", "Settings"). The defaults are the shipped values.
/// </summary>
public sealed record TurnRunnerOptions
{
    /// <summary>Most steps one turn may record: model calls, tool calls and the post. Doc 06 setting <c>runtime.turn_step_cap</c>.</summary>
    public int StepCap { get; init; } = 40;

    /// <summary>Most gateway-reported tokens one turn may use. Counted from usage events, cached tokens included.</summary>
    public long TokenCeiling { get; init; } = 200_000;

    /// <summary>Longest time one turn may run, measured on the runner's <see cref="TimeProvider"/>.</summary>
    public TimeSpan WallClock { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>How long a claimed lease lasts. The runner renews it before every model and tool call.</summary>
    public TimeSpan LeaseDuration { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Tool names whose calls may run again after a crash. Every other tool is non-idempotent, so an in-flight call is
    /// recorded as unknown and the model is told (doc 06, "Concurrency and recovery").
    /// </summary>
    public IReadOnlyCollection<string> IdempotentTools { get; init; } = [];

    /// <summary>
    /// The full tool catalogue. A run offers the model only the tools its <see cref="TurnRunRequest.ToolAllowlist"/> names.
    /// </summary>
    public IReadOnlyList<ModelToolDefinition> ToolCatalogue { get; init; } = [];

    /// <summary>Throws if a value cannot run a turn.</summary>
    public void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(StepCap, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(TokenCeiling, 1L);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(WallClock, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(LeaseDuration, TimeSpan.Zero);
        ArgumentNullException.ThrowIfNull(IdempotentTools);
        ArgumentNullException.ThrowIfNull(ToolCatalogue);
        foreach (var name in IdempotentTools)
            ArgumentException.ThrowIfNullOrWhiteSpace(name);
        foreach (var tool in ToolCatalogue)
            ArgumentException.ThrowIfNullOrWhiteSpace(tool.Name);
    }
}
