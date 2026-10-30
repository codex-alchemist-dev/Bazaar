using Bazaar.Contracts.Gateway;

namespace Bazaar.Server.Agents.Turns;

// Seams between the turn-loop work packages (doc 06 WP4, WP5, WP6, WP8). Each implementation lives in its own folder
// and is composed by the host. Tests code against the fakes, not against each other.
// TODO(component): move this folder into the agent-runtime Bazaar Component once PR #125 merges.

/// <summary>Inputs for building the model context of one turn (doc 06 "Context assembly").</summary>
public sealed record TurnContextRequest(
    Guid AgentId,
    Guid TurnId,
    TurnRole Role,
    string WakeReason,
    IReadOnlyList<Guid> InboxItemIds,
    string? TriggerGcId = null,
    long? TriggerGcSeq = null);

/// <summary>
/// The assembled context. <see cref="Messages"/> is the text the model sees. <see cref="ManifestJson"/> records ids,
/// version ids and hashes only, never text, so a turn can be reproduced (doc 06).
/// </summary>
public sealed record ContextPlan(IReadOnlyList<ModelMessage> Messages, string ManifestJson);

/// <summary>Builds the context for one turn under the section budgets (doc 06 "Context assembly").</summary>
public interface ITurnContextAssembler
{
    Task<ContextPlan> BuildAsync(TurnContextRequest request, CancellationToken cancellationToken);
}

/// <summary>Outcome of one tool call after policy, approval and logging (doc 06 "Tool dispatch", doc 15).</summary>
public enum ToolDispatchOutcome
{
    /// <summary>The tool ran; <see cref="ToolDispatchResult.ResultJson"/> holds its result.</summary>
    Executed,

    /// <summary>Policy refused the call. Nothing ran.</summary>
    Denied,

    /// <summary>The call needs an owner approval first. Nothing ran.</summary>
    PendingApproval,

    /// <summary>The call was in flight at a crash and is not idempotent. Recorded as unknown; the model is told (doc 06).</summary>
    Unknown,
}

/// <summary>A tool call the runtime asks the dispatcher to run for an agent turn.</summary>
public sealed record ToolDispatchRequest(
    Guid AgentId,
    Guid TurnId,
    Guid SessionId,
    TurnRole Role,
    ModelToolCall Call,
    bool Idempotent);

/// <summary>The result of one dispatch. <see cref="ResultJson"/> is set only for <see cref="ToolDispatchOutcome.Executed"/>.</summary>
public sealed record ToolDispatchResult(ToolDispatchOutcome Outcome, string? ResultJson = null, string? Reason = null);

/// <summary>Runs one tool call with grants, the taint gate and logging (doc 06 WP6, doc 15).</summary>
public interface IToolDispatcher
{
    Task<ToolDispatchResult> InvokeAsync(ToolDispatchRequest request, CancellationToken cancellationToken);
}

/// <summary>Where an agent's post goes. The implementation calls the GC append API so mention parsing and loop guards apply (doc 06 "Turn loop" step 5).</summary>
public interface IAgentPostSink
{
    Task PostAsync(Guid agentId, Guid turnId, string gcId, string text, CancellationToken cancellationToken);
}

/// <summary>How a turn ended. Set once per turn.</summary>
public enum TurnEndReason
{
    FinalMessage,
    StepCap,
    TokenCeiling,
    WallClock,
    Interrupted,
    GatewayFailed,
    LeaseLost,
}

/// <summary>Result of running one turn loop, including subagent children (doc 06 WP5, WP8).</summary>
public sealed record TurnOutcome(Guid TurnId, TurnEndReason Reason, string? Summary, int StepCount);

/// <summary>Parameters for one run. A subagent child sets <see cref="ParentTurnId"/> and <see cref="Depth"/>.</summary>
public sealed record TurnRunRequest(
    Guid AgentId,
    Guid SessionId,
    TurnRole Role,
    string WakeReason,
    IReadOnlyList<Guid> InboxItemIds,
    string Holder,
    string Route,
    IReadOnlyList<string> ToolAllowlist,
    Guid? ParentTurnId = null,
    int Depth = 0,
    string? TriggerGcId = null,
    long? TriggerGcSeq = null);

/// <summary>Runs one turn loop for an agent: claim, assemble, call the model, dispatch, post, commit (doc 06 "Turn loop").</summary>
public interface ITurnRunner
{
    Task<TurnOutcome> RunAsync(TurnRunRequest request, CancellationToken cancellationToken);
}
