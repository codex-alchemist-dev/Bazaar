namespace Bazaar.Server.Modules.LineMove;

/// <summary>How a step ended, as recorded in the audit trail.</summary>
public enum LineMoveAuditKind
{
    /// <summary>The step passed.</summary>
    Succeeded,

    /// <summary>The step failed, or the preflight rules did not pass.</summary>
    Failed,

    /// <summary>The run was cancelled during or before this step.</summary>
    Cancelled,

    /// <summary>Rollback was needed but outside the automatic window, or the rollback itself failed.</summary>
    ManualRollbackRequired,
}

/// <summary>
/// One audit event per step (doc 42 section 10.3: each step writes an audit event with the snapshot id and the
/// approving role). <see cref="SnapshotId"/> is null before the snapshot step succeeded.
/// </summary>
public sealed record LineMoveAuditEvent(
    string MoveId,
    LineMoveStep Step,
    LineMoveAuditKind Kind,
    int FromLine,
    int ToLine,
    string ApprovingRole,
    string? SnapshotId,
    string Detail,
    DateTimeOffset At);

/// <summary>Where line-move audit events go. The production writer appends to the audit log (doc 15); tests use an in-memory sink.</summary>
public interface IAuditSink
{
    /// <summary>Records one event. The runner passes <see cref="CancellationToken.None"/> so a cancelled run still gets its record.</summary>
    ValueTask RecordAsync(LineMoveAuditEvent auditEvent, CancellationToken cancellationToken);
}
