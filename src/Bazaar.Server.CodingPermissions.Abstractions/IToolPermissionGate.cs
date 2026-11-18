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
