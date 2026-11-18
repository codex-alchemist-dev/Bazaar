namespace Bazaar.Server.CodingPermissions;

/// <summary>The permission gate of the coding harness (doc 17, "Permissions"). Every tool call passes through it.</summary>
public interface IToolPermissionGate
{
    /// <summary>
    /// Judges one call. The call is recorded in the trace before this returns; if the record cannot be written the
    /// result is a denial.
    /// </summary>
    ValueTask<PermissionDecision> EvaluateAsync(ToolCallContext context, CancellationToken cancellationToken);
}

/// <summary>A gate decision with the id of the trace row the gate wrote for it.</summary>
/// <param name="Decision">The decision.</param>
/// <param name="CallId">The <c>tool_calls</c> row id; null when the gate wrote no row (the trace write failed).</param>
public sealed record GateEvaluation(PermissionDecision Decision, Guid? CallId);

/// <summary>A gate that reports the id of the row it wrote, so the outcome can complete that row.</summary>
public interface IIdentifiedToolPermissionGate : IToolPermissionGate
{
    /// <summary>Judges one call like <see cref="IToolPermissionGate.EvaluateAsync"/> and returns the row id too.</summary>
    ValueTask<GateEvaluation> EvaluateWithIdAsync(ToolCallContext context, CancellationToken cancellationToken);
}
