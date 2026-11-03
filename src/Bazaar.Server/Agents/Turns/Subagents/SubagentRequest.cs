namespace Bazaar.Server.Agents.Turns.Subagents;

/// <summary>
/// A request from a running parent turn for one child turn. The parent fields describe the turn that spawns the child.
/// </summary>
/// <param name="AgentId">The agent that owns the parent turn. The child runs as the same agent.</param>
/// <param name="SessionId">The parent's session. The child inherits it so taint propagates (doc 06 "Session key").</param>
/// <param name="ParentTurnId">The turn that spawns the child. Concurrency is counted per parent turn.</param>
/// <param name="ParentDepth">The parent's depth. The child runs at <c>ParentDepth + 1</c>.</param>
/// <param name="ParentToolAllowlist">The parent's tool allowlist. The child's allowlist must be a subset of it.</param>
/// <param name="ParentHolder">The parent's lease holder. The child gets a distinct holder derived from it.</param>
/// <param name="Prompt">The child's task text.</param>
/// <param name="ToolAllowlist">The tools the child asks for. Must be a subset of <paramref name="ParentToolAllowlist"/>.</param>
/// <param name="Route">The gateway route, from the parent's whitelist.</param>
/// <param name="ParentStepCap">The step cap the parent is configured with, or null for <see cref="SubagentOptions.StepCap"/>.</param>
/// <param name="ParentTriggerGcId">The GC that woke the parent. Passed to the child only when posting is allowed.</param>
/// <param name="ParentTriggerGcSeq">The sequence of the trigger message, paired with <paramref name="ParentTriggerGcId"/>.</param>
public sealed record SubagentRequest(
    Guid AgentId,
    Guid SessionId,
    Guid ParentTurnId,
    int ParentDepth,
    IReadOnlyList<string> ParentToolAllowlist,
    string ParentHolder,
    string Prompt,
    IReadOnlyList<string> ToolAllowlist,
    string Route,
    int? ParentStepCap = null,
    string? ParentTriggerGcId = null,
    long? ParentTriggerGcSeq = null);

/// <summary>
/// What the parent receives. The child's full trace stays in the turn tables; only its summary comes back here.
/// An abandoned child (<see cref="TurnEndReason.LeaseLost"/> or <see cref="TurnEndReason.GatewayFailed"/>) is returned as is.
/// </summary>
public sealed record SubagentResult(Guid ChildTurnId, TurnEndReason Reason, string? Summary, int StepCount);
