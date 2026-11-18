namespace Bazaar.Server.Vms.Reconciliation;

/// <summary>
/// Appends audit entries for the VM component. The production writer is <see cref="PgAuditWriter"/>. Entries are append-only:
/// there is no update or delete.
/// </summary>
public interface IAuditWriter
{
    Task WriteAsync(AuditRecord record, CancellationToken ct);
}
