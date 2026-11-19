namespace Bazaar.Server.AgentSafety.Drill;

/// <summary>
/// One drill run: when it started, how long it took, and the outcome of each step in order. Steps that did not run because an
/// earlier step failed are absent. Holds no secret values: step reasons are fixed text, and exception messages are never copied.
/// </summary>
/// <param name="Id">Unique id of the run. Time-ordered (UUIDv7).</param>
/// <param name="StartedAt">When the run started, from the drill's <see cref="TimeProvider"/>.</param>
/// <param name="Duration">Elapsed time of the run, including the cleanup.</param>
/// <param name="Steps">Each step's outcome, in run order.</param>
public sealed record DrillResult(Guid Id, DateTimeOffset StartedAt, TimeSpan Duration, IReadOnlyList<DrillStepResult> Steps)
{
    /// <summary>True when there is at least one step and every step passed.</summary>
    public bool Passed => Steps.Count > 0 && Steps.All(s => s.Passed);
}

/// <summary>The outcome of one drill step.</summary>
/// <param name="Name">The step name, for example <c>engage</c> or <c>containment</c>.</param>
/// <param name="Passed">True when the step passed.</param>
/// <param name="Reason">The failure text for a failed step, or "Passed." for a passed one.</param>
public sealed record DrillStepResult(string Name, bool Passed, string Reason);
