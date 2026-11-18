namespace Bazaar.Server.Security.Audit.Anchors;

/// <summary>
/// Reads the audit log for earlier anchor failures. <see cref="AuditAnchorService"/> asks it after a target anchors a checkpoint, so
/// an <c>audit.anchor.recovered</c> event is written even when the failure happened before a restart. Implemented by
/// <c>Db.PostgresAuditAnchorFailureLog</c>.
/// </summary>
public interface IAuditAnchorFailureLog
{
    /// <summary>True when an <c>audit.anchor.failed</c> row exists for this checkpoint (by last sequence) and target.</summary>
    Task<bool> HasRecordedFailureAsync(long lastSeq, string targetId, CancellationToken ct = default);
}
