using Bazaar.Contracts.Coding;

namespace Bazaar.Server.CodingPermissions;

/// <summary>The answer of the generic tool policy for one call.</summary>
/// <param name="Outcome">Allow, require approval, or deny.</param>
/// <param name="Reason">Why, in words for the trace and the approver.</param>
/// <param name="ApprovedByGrant">True when a verified per-action approval covered the call.</param>
/// <param name="RateLimited">True when the call was denied because its quota window is full. The gate then freezes the agent.</param>
public sealed record GenericPolicyDecision(
    PermissionOutcome Outcome,
    string Reason,
    bool ApprovedByGrant = false,
    bool RateLimited = false);

/// <summary>
/// Port over Server's generic tool policy (<c>ToolPolicyEngine</c>: class and tier table, taint, approvals, rate buckets,
/// doc 15). A host adapter binds it; this module adds only the doc 17 rules on top.
/// </summary>
public interface IGenericToolPolicy
{
    /// <summary>Judges one call under the doc 15 rules. An allowed call counts against its quota.</summary>
    GenericPolicyDecision Evaluate(ToolCallContext context);
}

/// <summary>Append-only <c>tool_calls</c> trace (doc 17, tables). Rows have the shape of <see cref="ToolCallRecord"/>.</summary>
public interface IToolCallTrace
{
    /// <summary>Appends one row. A throw means the row was not written.</summary>
    ValueTask AppendAsync(ToolCallRecord record, CancellationToken cancellationToken);

    /// <summary>
    /// Records the outcome of a row appended by <see cref="AppendAsync"/>. The table allows this transition once.
    /// </summary>
    /// <param name="id">The row id.</param>
    /// <param name="status">The outcome; <see cref="ToolCallStatus.Unknown"/> is not an outcome and is rejected.</param>
    /// <param name="exitCode">The process exit code, when there was one.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>True when the row was completed; false when it does not exist or already has an outcome. A throw means the store failed.</returns>
    ValueTask<bool> CompleteAsync(Guid id, ToolCallStatus status, int? exitCode, CancellationToken cancellationToken);
}

/// <summary>Freezes an agent and notifies the owner (doc 17: a rate-cap breach freezes the agent and notifies the owner).</summary>
public interface IAgentFreeze
{
    /// <summary>True while the agent is frozen.</summary>
    ValueTask<bool> IsFrozenAsync(string agentId, CancellationToken cancellationToken);

    /// <summary>Freezes the agent and notifies the owner with <paramref name="reason"/>.</summary>
    ValueTask FreezeAsync(string agentId, string reason, CancellationToken cancellationToken);
}
