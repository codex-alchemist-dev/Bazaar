namespace Bazaar.Server.Vms.Reconciliation;

/// <summary>
/// One audit entry from the VM component. <see cref="Action"/> is stored in <c>audit_log.kind</c>; <see cref="Subject"/> is the
/// VM id and is stored inside the payload. <see cref="Detail"/> must be a JSON document.
/// </summary>
public sealed record AuditRecord(string Action, Guid Subject, string Detail, DateTimeOffset At);

/// <summary>The audit actions the VM reconciler writes (doc 16, "Behaviour and edge cases"; doc 15, audit log).</summary>
public static class VmAuditActions
{
    /// <summary>The recorded state differs from the backend's. Written before any change is made.</summary>
    public const string StateMismatch = "vm.reconcile.state_mismatch";

    /// <summary>The backend's state cannot be reached from the recorded state. The row is left as it is.</summary>
    public const string UnexpectedState = "vm.reconcile.unexpected_state";

    /// <summary>The backend threw while reporting a VM's state. The row is left as it is.</summary>
    public const string BackendError = "vm.reconcile.backend_error";

    /// <summary>A backend VM has no record. It is quarantined, never deleted.</summary>
    public const string OrphanBackendVm = "vm.reconcile.orphan_backend_vm";
}
