namespace Bazaar.Server.Agents.Turns;

/// <summary>
/// What a running turn holds (doc 06, "Running-turn registry"). Read-only, and it carries no message text: only ids, the lease
/// holder, limits and the route.
/// </summary>
/// <param name="TurnId">The turn's id; the registry key.</param>
/// <param name="AgentId">The agent that owns the turn.</param>
/// <param name="SessionId">The turn's session; a subagent inherits it so taint propagates.</param>
/// <param name="LeaseHolder">The holder that owns the agent's lease for this turn.</param>
/// <param name="ToolAllowlist">The tools the turn may call. A copy, never the caller's list.</param>
/// <param name="Depth">The subagent depth; zero for a top-level turn.</param>
/// <param name="StepCap">The most steps the turn may record.</param>
/// <param name="TriggerGcId">The GC that woke the turn, or null.</param>
/// <param name="TriggerGcSeq">The trigger message sequence, paired with <paramref name="TriggerGcId"/>.</param>
/// <param name="Route">The model route; blank means the agent's policy default.</param>
public sealed record RunningTurnInfo(
    Guid TurnId,
    Guid AgentId,
    Guid SessionId,
    string LeaseHolder,
    IReadOnlyList<string> ToolAllowlist,
    int Depth,
    int StepCap,
    string? TriggerGcId,
    long? TriggerGcSeq,
    string Route);
