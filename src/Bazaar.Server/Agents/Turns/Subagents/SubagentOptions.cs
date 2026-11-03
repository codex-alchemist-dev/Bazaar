namespace Bazaar.Server.Agents.Turns.Subagents;

/// <summary>
/// Operator limits for runtime subagents (doc 06 "Subagents" and "Settings"). They are settings, so an install may
/// tighten them; the shipped defaults are the documented ones.
/// </summary>
public sealed record SubagentOptions
{
    /// <summary>Deepest child turn allowed. The parent turn is depth 0, so the default allows children and grandchildren.</summary>
    public int MaxDepth { get; init; } = 2;

    /// <summary>Children one parent turn may have running at once.</summary>
    public int MaxConcurrent { get; init; } = 3;

    /// <summary>The step ceiling for each child. A child never runs with more steps than its parent.</summary>
    public int StepCap { get; init; } = 40;

    /// <summary>Whether children may post to the GC of the trigger. Off by default (<c>runtime.subagent_may_post</c>).</summary>
    public bool MayPost { get; init; }

    /// <summary>Checks the limits. Throws on the first one below its minimum.</summary>
    /// <exception cref="ArgumentOutOfRangeException">A limit is below 1.</exception>
    public void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxDepth, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxConcurrent, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(StepCap, 1);
    }
}
