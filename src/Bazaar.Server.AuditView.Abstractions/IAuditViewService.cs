namespace Bazaar.Server.AuditView;

/// <summary>
/// Read-only, role-scoped view of the audit log (doc 48 section 5.4). It has no write or delete method, and a view never
/// changes the chain. Every call, allowed or refused, is itself audited as an <c>audit.view</c> row.
/// </summary>
public interface IAuditViewService
{
    Task<AuditViewPage> ViewAsync(AuditViewer viewer, AuditViewQuery query, CancellationToken ct = default);
}

/// <summary>Maps a principal's roles to the event-kind categories it may see. Fails closed: unknown means denied.</summary>
public interface IAuditViewScopeResolver
{
    Task<AuditViewScope> ResolveAsync(AuditViewer viewer, CancellationToken ct = default);
}

/// <summary>
/// Read port over the audit chain. It only reads. The host adapts the <c>audit_log</c> table to it. The service filters by
/// scope itself and does not trust the source to do so.
/// </summary>
public interface IAuditViewSource
{
    /// <summary>Rows in ascending sequence order that satisfy the query. Fewer than <c>Limit</c> rows means the log ended.</summary>
    Task<IReadOnlyList<AuditSourceRow>> ReadAsync(AuditSourceQuery query, CancellationToken ct = default);
}
