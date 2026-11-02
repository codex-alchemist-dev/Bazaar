using System.Text.Json.Nodes;
using Bazaar.Server.Vms.Environments;
using Bazaar.Server.Vms.Lifecycle;

namespace Bazaar.Server.Vms.Reconciliation;

/// <summary>
/// Startup reconciliation (doc 16, "Behaviour and edge cases"). It compares the recorded state of each non-retired VM with the
/// state the backend reports, and records what it finds in the audit log. A mismatch that the transition table allows is
/// corrected. A mismatch it forbids is audited and left for an operator. A backend error is audited and the row is left alone.
/// Nothing is ever deleted, and the backend is only asked for state.
/// </summary>
/// <remarks>
/// Gap: <see cref="IVmBackend"/> has no operation that lists the VMs the backend holds. The caller supplies the backend's
/// unknown handles to <see cref="QuarantineAsync"/>. The Hyper-V provider must add that enumeration.
/// </remarks>
public sealed class VmReconciler(
    EnvironmentRepository repository,
    IVmBackend backend,
    VmOperationLocks locks,
    IAuditWriter audit,
    TimeProvider clock)
{
    /// <summary>
    /// Compares every non-retired VM with the backend. Each VM is checked under its operation lock, so a lifecycle operation
    /// and a reconciliation never overlap on one VM. A VM without a backend ref has never reached the backend, so it is skipped.
    /// </summary>
    public async Task<ReconcileReport> ReconcileAsync(CancellationToken ct)
    {
        var listed = await repository.ListNonRetiredVmsAsync(ct).ConfigureAwait(false);
        var actions = new List<string>();
        var matched = 0;
        var corrected = 0;
        var skipped = 0;
        var errors = 0;

        foreach (var candidate in listed)
        {
            using var lease = await locks.AcquireAsync(candidate.Id, ct).ConfigureAwait(false);

            // Read again under the lock: the snapshot may be stale by the time the lock is taken.
            var vm = await repository.GetVmAsync(candidate.Id, ct).ConfigureAwait(false);
            if (vm is null || vm.State == VmState.Retired || vm.BackendRef is null)
            {
                skipped++;
                continue;
            }

            VmState actual;
            try
            {
                actual = await backend.GetStateAsync(new VmHandle(vm.Id, vm.BackendRef), ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                var detail = new JsonObject
                {
                    ["recorded"] = EnvironmentText.ToDb(vm.State),
                    ["message"] = ex.Message,
                };
                await AuditAsync(actions, VmAuditActions.BackendError, vm.Id, detail, ct).ConfigureAwait(false);
                errors++;
                continue;
            }

            if (actual == vm.State)
            {
                matched++;
                continue;
            }

            var mismatch = new JsonObject
            {
                ["recorded"] = EnvironmentText.ToDb(vm.State),
                ["actual"] = EnvironmentText.ToDb(actual),
            };
            await AuditAsync(actions, VmAuditActions.StateMismatch, vm.Id, mismatch, ct).ConfigureAwait(false);

            if (!VmTransitions.CanTransition(vm.State, actual))
            {
                await AuditAsync(actions, VmAuditActions.UnexpectedState, vm.Id, mismatch, ct).ConfigureAwait(false);
                skipped++;
                continue;
            }

            // Compare-and-set on the recorded state. Under the lock this only fails if a writer outside the lifecycle service changed the row.
            if (await repository.TryUpdateVmStateAsync(vm.Id, vm.State, actual, clock.GetUtcNow(), ct).ConfigureAwait(false))
                corrected++;
            else
                skipped++;
        }

        return new ReconcileReport(listed.Count, matched, corrected, skipped, errors, actions);
    }

    /// <summary>
    /// Audits each backend VM that has no record. Nothing is deleted or changed. A handle whose VM id does have a record is not
    /// an orphan, so it is passed over and not audited. Returns the handles that were quarantined.
    /// </summary>
    public async Task<IReadOnlyList<VmHandle>> QuarantineAsync(IEnumerable<VmHandle> unknown, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(unknown);

        var quarantined = new List<VmHandle>();
        foreach (var handle in unknown)
        {
            if (await repository.GetVmAsync(handle.VmId, ct).ConfigureAwait(false) is not null)
                continue;

            var detail = new JsonObject { ["backend_ref"] = handle.BackendRef };
            await audit.WriteAsync(
                new AuditRecord(VmAuditActions.OrphanBackendVm, handle.VmId, detail.ToJsonString(), clock.GetUtcNow()),
                ct).ConfigureAwait(false);
            quarantined.Add(handle);
        }
        return quarantined;
    }

    private async Task AuditAsync(List<string> actions, string action, Guid subject, JsonObject detail, CancellationToken ct)
    {
        await audit.WriteAsync(new AuditRecord(action, subject, detail.ToJsonString(), clock.GetUtcNow()), ct).ConfigureAwait(false);
        actions.Add(action);
    }
}
