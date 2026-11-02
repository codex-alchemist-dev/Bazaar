namespace Bazaar.Server.Vms.Reconciliation;

/// <summary>
/// What one <see cref="VmReconciler.ReconcileAsync"/> run did. Every checked VM lands in exactly one of matched, corrected,
/// skipped or errors. <see cref="AuditActions"/> lists the audit actions written, in the order they were written.
/// </summary>
public sealed record ReconcileReport(
    int Checked,
    int Matched,
    int Corrected,
    int Skipped,
    int Errors,
    IReadOnlyList<string> AuditActions);
