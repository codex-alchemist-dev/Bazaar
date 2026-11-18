using Bazaar.Server.Vms.Environments;

namespace Bazaar.Server.Vms.Lifecycle;

/// <summary>
/// The single entry point for VM lifecycle changes (doc 16). Each operation takes the VM's lock, reads the recorded state,
/// checks the transition table, calls the backend, then records the new state with a compare-and-set. A refused transition
/// never reaches the backend. If the backend throws, the recorded state stays as it was and the exception propagates.
/// </summary>
public sealed class VmLifecycleService(IEnvironmentVmStore store, IVmBackend backend, VmOperationLocks locks, TimeProvider clock)
{
    /// <summary>
    /// Creates the child VM for <paramref name="vm"/>, which must be in <see cref="VmState.Absent"/>. The row is inserted,
    /// moved to Provisioning, and ends in Stopped. If the backend fails, the row goes back to Absent and the exception propagates.
    /// The environment id stays the store's id; the backend's own id is kept only as <c>backend_ref</c>.
    /// </summary>
    public async Task<VmOperationResult> CreateAsync(EnvironmentVmRecord vm, CancellationToken ct)
    {
        if (vm.State != VmState.Absent)
            return Refuse(VmRefusalReason.ForbiddenTransition, vm.State, VmState.Provisioning);

        using var lease = await locks.AcquireAsync(vm.Id, ct).ConfigureAwait(false);
        await store.InsertAsync(vm, ct).ConfigureAwait(false);
        if (!await store.TryUpdateStateAsync(vm.Id, VmState.Absent, VmState.Provisioning, clock.GetUtcNow(), ct).ConfigureAwait(false))
            return await StateChangedAsync(vm.Id, VmState.Provisioning, ct).ConfigureAwait(false);

        VmHandle handle;
        try
        {
            var spec = new VmSpec(vm.Os, vm.Vcpu, vm.MemoryMb, vm.DiskCapGb);
            handle = await backend.CreateChildAsync(new ImageVersionId(vm.ParentImageId), spec, ct).ConfigureAwait(false);
        }
        catch
        {
            // Create failed: the table sends Provisioning back to Absent. Recorded with no cancellation so the row is not left in Provisioning.
            await store.TryUpdateStateAsync(vm.Id, VmState.Provisioning, VmState.Absent, clock.GetUtcNow(), CancellationToken.None).ConfigureAwait(false);
            throw;
        }

        // The backend created the VM, so the handle is recorded and the state moves even if the caller has been cancelled.
        if (!await store.SetBackendRefAsync(vm.Id, handle.BackendRef, clock.GetUtcNow(), CancellationToken.None).ConfigureAwait(false))
            throw new InvalidOperationException($"VM {vm.Id} disappeared while it was being created.");
        if (!await store.TryUpdateStateAsync(vm.Id, VmState.Provisioning, VmState.Stopped, clock.GetUtcNow(), CancellationToken.None).ConfigureAwait(false))
            return await StateChangedAsync(vm.Id, VmState.Stopped, ct).ConfigureAwait(false);
        return new VmOperationResult.Applied(VmState.Stopped);
    }

    public Task<VmOperationResult> StartAsync(Guid vmId, CancellationToken ct) =>
        RunAsync(vmId, VmState.Running, h => backend.StartAsync(h, ct), ct);

    public Task<VmOperationResult> StopAsync(Guid vmId, StopMode mode, CancellationToken ct) =>
        RunAsync(vmId, VmState.Stopped, h => backend.StopAsync(h, mode, ct), ct);

    public Task<VmOperationResult> SaveAsync(Guid vmId, CancellationToken ct) =>
        RunAsync(vmId, VmState.Saved, h => backend.SaveStateAsync(h, ct), ct);

    public async Task<VmCheckpointResult> CheckpointAsync(Guid vmId, CancellationToken ct)
    {
        CheckpointId? checkpoint = null;
        var result = await RunAsync(vmId, VmState.Checkpointed, async h =>
        {
            checkpoint = await backend.CheckpointAsync(h, ct).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);

        if (result is VmOperationResult.Refused refused)
            return new VmCheckpointResult.Refused(refused.Refusal);
        return new VmCheckpointResult.Applied(checkpoint!.Value);
    }

    public Task<VmOperationResult> RestoreAsync(Guid vmId, CheckpointId checkpoint, CancellationToken ct) =>
        RunAsync(vmId, VmState.Restored, h => backend.RestoreAsync(h, checkpoint, ct), ct);

    public Task<VmOperationResult> RetireAsync(Guid vmId, CancellationToken ct) =>
        RunAsync(vmId, VmState.Retired, h => backend.RetireAsync(h, ct), ct);

    /// <summary>
    /// Returns the recorded state, or null for an unknown VM. This reads the store only: it takes no lock and calls no backend.
    /// </summary>
    public async Task<VmState?> GetStateAsync(Guid vmId, CancellationToken ct) =>
        (await store.GetAsync(vmId, ct).ConfigureAwait(false))?.State;

    private async Task<VmOperationResult> RunAsync(Guid vmId, VmState requested, Func<VmHandle, Task> backendCall, CancellationToken ct)
    {
        using var lease = await locks.AcquireAsync(vmId, ct).ConfigureAwait(false);

        var current = await store.GetAsync(vmId, ct).ConfigureAwait(false);
        if (current is null)
            return Refuse(VmRefusalReason.UnknownVm, null, requested);
        if (!VmTransitions.CanTransition(current.State, requested))
            return Refuse(VmRefusalReason.ForbiddenTransition, current.State, requested);
        if (current.BackendRef is null)
            throw new InvalidOperationException($"VM {vmId} is {current.State} but has no backend ref.");

        // An exception here propagates and leaves the recorded state unchanged.
        await backendCall(new VmHandle(vmId, current.BackendRef)).ConfigureAwait(false);

        // The backend has acted, so the outcome is recorded even if the caller has been cancelled.
        // A failed compare-and-set means the row changed during the call; it is reported, not claimed as success.
        if (!await store.TryUpdateStateAsync(vmId, current.State, requested, clock.GetUtcNow(), CancellationToken.None).ConfigureAwait(false))
            return await StateChangedAsync(vmId, requested, ct).ConfigureAwait(false);
        return new VmOperationResult.Applied(requested);
    }

    private async Task<VmOperationResult> StateChangedAsync(Guid vmId, VmState requested, CancellationToken ct)
    {
        var now = await store.GetAsync(vmId, ct).ConfigureAwait(false);
        return Refuse(VmRefusalReason.StateChanged, now?.State, requested);
    }

    private static VmOperationResult.Refused Refuse(VmRefusalReason reason, VmState? current, VmState? requested) =>
        new VmOperationResult.Refused(new VmRefusal(reason, current, requested));
}
