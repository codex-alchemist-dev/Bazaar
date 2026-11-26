using Bazaar.Server.VmDensity.Abstractions;
using Bazaar.Server.Vms.Environments;

namespace Bazaar.Server.Vms.Lifecycle;

/// <summary>
/// The single entry point for VM lifecycle changes (doc 16). Each operation takes the VM's lock, reads the recorded state,
/// checks the transition table, asks the router for the VM's backend (doc 62 section 9.2), calls it, then records the new state
/// with a compare-and-set. A refused transition or a missing provider never reaches a backend. If the backend throws, the recorded
/// state stays as it was and the exception propagates. A start is first admitted by the optional density governor and counted against
/// the optional device-guest cap (doc 62 section 9.3); either refusal is <see cref="VmRefusalReason.Capacity"/>.
/// </summary>
public sealed class VmLifecycleService
{
    private readonly IEnvironmentVmStore _store;
    private readonly IVmBackendRouter _router;
    private readonly VmOperationLocks _locks;
    private readonly TimeProvider _clock;
    private readonly IDensityGovernor? _governor;
    private readonly IDeviceGuestPolicy? _devicePolicy;
    private readonly IVmActivitySource? _activity;

    // The lock key that serialises counting and reserving vms.devices.max_concurrent slots. No VM has this id (ids are version 7 GUIDs).
    private static readonly Guid _deviceCapLockKey = Guid.AllBitsSet;

    // Device starts that hold a slot of vms.devices.max_concurrent. Read and released under _deviceSync, never held across an await.
    private readonly Lock _deviceSync = new();
    private readonly HashSet<Guid> _deviceStarting = [];

    /// <summary>One backend for every VM (the doc 16 single-backend lifecycle). No provider kind is recorded.</summary>
    public VmLifecycleService(IEnvironmentVmStore store, IVmBackend backend, VmOperationLocks locks, TimeProvider clock)
        : this(store, new SingleBackendRouter(backend), locks, clock)
    {
    }

    /// <summary>Several providers: each VM goes to the backend the router names.</summary>
    /// <param name="store">The VM rows.</param>
    /// <param name="router">Finds each VM's backend.</param>
    /// <param name="locks">Per-VM operation locks.</param>
    /// <param name="clock">The clock.</param>
    /// <param name="governor">The density governor, or null when no <c>vm-density</c> component is registered: starts are then not admitted.</param>
    /// <param name="devicePolicy">The live device-guest limits, or null when no device module is registered: no aggregate device cap.</param>
    /// <param name="activity">What VMs are doing, or null: activity flags are false and last use is the last state change.</param>
    public VmLifecycleService(
        IEnvironmentVmStore store,
        IVmBackendRouter router,
        VmOperationLocks locks,
        TimeProvider clock,
        IDensityGovernor? governor = null,
        IDeviceGuestPolicy? devicePolicy = null,
        IVmActivitySource? activity = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _router = router ?? throw new ArgumentNullException(nameof(router));
        _locks = locks ?? throw new ArgumentNullException(nameof(locks));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _governor = governor;
        _devicePolicy = devicePolicy;
        _activity = activity;
    }

    /// <summary>
    /// Creates the child VM for <paramref name="vm"/>, which must be in <see cref="VmState.Absent"/>. The router picks the provider
    /// first (by <see cref="EnvironmentVmRecord.ProviderKind"/>, or by the guest OS); if none may serve, the result is refused with
    /// <see cref="VmRefusalReason.NoProvider"/> and no row is written. Otherwise the row is inserted with the chosen kind, moved to
    /// Provisioning, and ends in Stopped. If the backend fails, the row goes back to Absent and the exception propagates.
    /// The environment id stays the store's id; the backend's own id is kept only as <c>backend_ref</c>.
    /// </summary>
    public async Task<VmOperationResult> CreateAsync(EnvironmentVmRecord vm, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(vm);
        if (vm.State != VmState.Absent)
            return Refuse(VmRefusalReason.ForbiddenTransition, vm.State, VmState.Provisioning);

        using var lease = await _locks.AcquireAsync(vm.Id, ct).ConfigureAwait(false);
        var route = await _router.RouteNewAsync(vm.Os, vm.ProviderKind, ct).ConfigureAwait(false);
        if (route.Backend is null)
            return Refuse(VmRefusalReason.NoProvider, vm.State, VmState.Provisioning, route.Reason);

        var row = vm with { ProviderKind = route.Kind };
        await _store.InsertAsync(row, ct).ConfigureAwait(false);
        if (!await _store.TryUpdateStateAsync(vm.Id, VmState.Absent, VmState.Provisioning, _clock.GetUtcNow(), ct).ConfigureAwait(false))
            return await StateChangedAsync(vm.Id, VmState.Provisioning, ct).ConfigureAwait(false);

        VmHandle handle;
        try
        {
            var spec = new VmSpec(vm.Os, vm.Vcpu, vm.MemoryMb, vm.DiskCapGb);
            handle = await route.Backend.CreateChildAsync(new ImageVersionId(vm.ParentImageId), spec, ct).ConfigureAwait(false);
        }
        catch
        {
            // Create failed: the table sends Provisioning back to Absent. Recorded with no cancellation so the row is not left in Provisioning.
            await _store.TryUpdateStateAsync(vm.Id, VmState.Provisioning, VmState.Absent, _clock.GetUtcNow(), CancellationToken.None).ConfigureAwait(false);
            throw;
        }

        // The child lives in this backend object, so the VM stays on it even if the provider hands out a new one later.
        _router.Pin(vm.Id, route.Kind, route.Backend);

        // The backend created the VM, so the handle is recorded and the state moves even if the caller has been cancelled.
        if (!await _store.SetBackendRefAsync(vm.Id, handle.BackendRef, _clock.GetUtcNow(), CancellationToken.None).ConfigureAwait(false))
            throw new InvalidOperationException($"VM {vm.Id} disappeared while it was being created.");
        if (!await _store.TryUpdateStateAsync(vm.Id, VmState.Provisioning, VmState.Stopped, _clock.GetUtcNow(), CancellationToken.None).ConfigureAwait(false))
            return await StateChangedAsync(vm.Id, VmState.Stopped, ct).ConfigureAwait(false);
        return new VmOperationResult.Applied(VmState.Stopped);
    }

    /// <summary>
    /// Starts or resumes a VM (doc 62 section 9.3). A device guest first takes a slot of <c>vms.devices.max_concurrent</c>; then the
    /// density governor admits the start, before the VM's lock is taken so a queued start never blocks a save the governor chose. The
    /// ticket is confirmed after a successful start and released on any other outcome.
    /// </summary>
    public async Task<VmOperationResult> StartAsync(Guid vmId, CancellationToken ct)
    {
        if (_governor is null && _devicePolicy is null)
            return await RunAsync(vmId, VmState.Running, (b, h) => b.StartAsync(h, ct), ct).ConfigureAwait(false);

        // Cheap refusals first, so an unknown VM or a forbidden start never waits in the governor's queue. RunAsync checks again under the lock.
        var current = await _store.GetAsync(vmId, ct).ConfigureAwait(false);
        if (current is null)
            return Refuse(VmRefusalReason.UnknownVm, null, VmState.Running);
        if (!VmTransitions.CanTransition(current.State, VmState.Running))
            return Refuse(VmRefusalReason.ForbiddenTransition, current.State, VmState.Running);
        var route = await _router.RouteExistingAsync(current, ct).ConfigureAwait(false);
        if (route.Backend is null)
            return Refuse(VmRefusalReason.NoProvider, current.State, VmState.Running, route.Reason);

        bool ownsSlot = false;
        try
        {
            if (_devicePolicy is not null && _router.IsOnDemand(route.Kind ?? current.ProviderKind))
            {
                var (owns, refusal) = await TryReserveDeviceSlotAsync(current, ct).ConfigureAwait(false);
                if (refusal is not null)
                    return refusal;
                ownsSlot = owns;
            }

            AdmissionTicket? ticket = null;
            if (_governor is not null)
            {
                try
                {
                    ticket = await _governor.AdmitAsync(ToDensityVm(current), ct).ConfigureAwait(false);
                }
                catch (DensityAdmissionException e)
                {
                    return Refuse(VmRefusalReason.Capacity, current.State, VmState.Running, $"The density governor refused the start ({e.Reason}): {e.Message}");
                }
            }

            // Disposing a confirmed ticket does nothing; disposing an unconfirmed one releases its reservation.
            await using (ticket)
            {
                var result = await RunAsync(vmId, VmState.Running, (b, h) => b.StartAsync(h, ct), ct).ConfigureAwait(false);
                if (result is VmOperationResult.Applied && ticket is not null)
                    await ticket.ConfirmStartedAsync().ConfigureAwait(false);
                return result;
            }
        }
        finally
        {
            if (ownsSlot)
            {
                lock (_deviceSync)
                    _deviceStarting.Remove(vmId);
            }
        }
    }

    public async Task<VmOperationResult> StopAsync(Guid vmId, StopMode mode, CancellationToken ct) =>
        MemoryChanged(await RunAsync(vmId, VmState.Stopped, (b, h) => b.StopAsync(h, mode, ct), ct).ConfigureAwait(false));

    public async Task<VmOperationResult> SaveAsync(Guid vmId, CancellationToken ct) =>
        MemoryChanged(await RunAsync(vmId, VmState.Saved, (b, h) => b.SaveStateAsync(h, ct), ct).ConfigureAwait(false));

    public async Task<VmCheckpointResult> CheckpointAsync(Guid vmId, CancellationToken ct)
    {
        CheckpointId? checkpoint = null;
        var result = await RunAsync(vmId, VmState.Checkpointed, async (b, h) =>
        {
            checkpoint = await b.CheckpointAsync(h, ct).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);

        if (result is VmOperationResult.Refused refused)
            return new VmCheckpointResult.Refused(refused.Refusal);
        return new VmCheckpointResult.Applied(checkpoint!.Value);
    }

    public Task<VmOperationResult> RestoreAsync(Guid vmId, CheckpointId checkpoint, CancellationToken ct) =>
        RunAsync(vmId, VmState.Restored, (b, h) => b.RestoreAsync(h, checkpoint, ct), ct);

    public async Task<VmOperationResult> RetireAsync(Guid vmId, CancellationToken ct)
    {
        var result = await RunAsync(vmId, VmState.Retired, (b, h) => b.RetireAsync(h, ct), ct).ConfigureAwait(false);
        if (result is VmOperationResult.Applied)
            _router.Release(vmId);
        return MemoryChanged(result);
    }

    /// <summary>
    /// The VMs whose row is Running, each with its provider kind, whether it is a device guest, whether its own backend can save it, and
    /// its activity (doc 62 section 9.3). A VM no provider can serve is listed with no save support. Reads the store and the activity
    /// source; calls no backend operation.
    /// </summary>
    public async Task<IReadOnlyList<RunningVmInfo>> ListRunningAsync(CancellationToken ct)
    {
        var rows = await _store.ListNonRetiredAsync(ct).ConfigureAwait(false);
        var running = new List<RunningVmInfo>();
        foreach (var row in rows)
        {
            if (row.State != VmState.Running)
                continue;
            var route = await _router.RouteExistingAsync(row, ct).ConfigureAwait(false);
            var kind = route.Kind ?? row.ProviderKind;
            var activity = await ActivityOfAsync(row, ct).ConfigureAwait(false);
            running.Add(new RunningVmInfo(row, kind, _router.IsOnDemand(kind), route.Backend?.SupportsSaveState ?? false, activity));
        }

        return running;
    }

    /// <summary>The governor's view of a VM: memory is the row's <c>memory_mb</c>, the value the backend got in its <see cref="VmSpec"/>.</summary>
    public static DensityVm ToDensityVm(EnvironmentVmRecord vm)
    {
        ArgumentNullException.ThrowIfNull(vm);
        return new DensityVm(vm.Id, vm.AgentId.ToString(), ToDensityOs(vm.Os), vm.MemoryMb);
    }

    /// <summary>Maps the guest OS. A microVM guest is a Linux guest; Android has its own rank in the save order.</summary>
    public static DensityGuestOs ToDensityOs(VmOs os) => os switch
    {
        VmOs.Windows => DensityGuestOs.Windows,
        VmOs.Linux => DensityGuestOs.Linux,
        VmOs.Android => DensityGuestOs.Android,
        _ => throw new ArgumentOutOfRangeException(nameof(os), os, null),
    };

    /// <summary>
    /// Returns the recorded state, or null for an unknown VM. This reads the store only: it takes no lock and calls no backend.
    /// </summary>
    public async Task<VmState?> GetStateAsync(Guid vmId, CancellationToken ct) =>
        (await _store.GetAsync(vmId, ct).ConfigureAwait(false))?.State;

    /// <summary>
    /// Whether the backend that owns this VM can save a running VM's state (<see cref="IVmBackend.SupportsSaveState"/>). Null for an
    /// unknown VM or one no provider can serve. Reads the store and asks the router; calls no backend operation.
    /// </summary>
    public async Task<bool?> SupportsSaveStateAsync(Guid vmId, CancellationToken ct)
    {
        var vm = await _store.GetAsync(vmId, ct).ConfigureAwait(false);
        if (vm is null)
            return null;
        var route = await _router.RouteExistingAsync(vm, ct).ConfigureAwait(false);
        return route.Backend?.SupportsSaveState;
    }

    private async Task<VmOperationResult> RunAsync(Guid vmId, VmState requested, Func<IVmBackend, VmHandle, Task> backendCall, CancellationToken ct)
    {
        using var lease = await _locks.AcquireAsync(vmId, ct).ConfigureAwait(false);

        var current = await _store.GetAsync(vmId, ct).ConfigureAwait(false);
        if (current is null)
            return Refuse(VmRefusalReason.UnknownVm, null, requested);
        if (!VmTransitions.CanTransition(current.State, requested))
            return Refuse(VmRefusalReason.ForbiddenTransition, current.State, requested);
        if (current.BackendRef is null)
            throw new InvalidOperationException($"VM {vmId} is {current.State} but has no backend ref.");

        var route = await _router.RouteExistingAsync(current, ct).ConfigureAwait(false);
        if (route.Backend is null)
            return Refuse(VmRefusalReason.NoProvider, current.State, requested, route.Reason);

        // An exception here propagates and leaves the recorded state unchanged.
        await backendCall(route.Backend, new VmHandle(vmId, current.BackendRef)).ConfigureAwait(false);

        // The backend has acted, so the outcome is recorded even if the caller has been cancelled.
        // A failed compare-and-set means the row changed during the call; it is reported, not claimed as success.
        if (!await _store.TryUpdateStateAsync(vmId, current.State, requested, _clock.GetUtcNow(), CancellationToken.None).ConfigureAwait(false))
            return await StateChangedAsync(vmId, requested, ct).ConfigureAwait(false);
        return new VmOperationResult.Applied(requested);
    }

    // Counts device guests that run or are starting and takes a slot when one is free. Serialised, so two starts cannot both take the
    // last slot. Returns whether this call holds the slot; a concurrent start of the same VM holds none and is refused under the lock.
    private async Task<(bool OwnsSlot, VmOperationResult? Refusal)> TryReserveDeviceSlotAsync(EnvironmentVmRecord vm, CancellationToken ct)
    {
        int cap = await _devicePolicy!.GetMaxConcurrentAsync(ct).ConfigureAwait(false);
        if (cap <= 0)
            return (false, null);

        using var lease = await _locks.AcquireAsync(_deviceCapLockKey, ct).ConfigureAwait(false);
        var rows = await _store.ListNonRetiredAsync(ct).ConfigureAwait(false);
        lock (_deviceSync)
        {
            int busy = _deviceStarting.Count + rows.Count(r =>
                r.State == VmState.Running && !_deviceStarting.Contains(r.Id) && _router.IsOnDemand(r.ProviderKind));
            if (busy >= cap)
            {
                return (false, Refuse(
                    VmRefusalReason.Capacity, vm.State, VmState.Running,
                    $"vms.devices.max_concurrent ({cap}) device guests are already running or starting."));
            }

            return (_deviceStarting.Add(vm.Id), null);
        }
    }

    private async ValueTask<VmActivity> ActivityOfAsync(EnvironmentVmRecord vm, CancellationToken ct)
    {
        var known = _activity is null ? null : await _activity.GetAsync(vm.Id, ct).ConfigureAwait(false);
        if (known is null)
            return new VmActivity(false, false, false, vm.UpdatedAt);
        return known with { LastUsedAt = known.LastUsedAt > vm.UpdatedAt ? known.LastUsedAt : vm.UpdatedAt };
    }

    private VmOperationResult MemoryChanged(VmOperationResult result)
    {
        if (result is VmOperationResult.Applied)
            _governor?.NotifyMemoryChanged();
        return result;
    }

    private async Task<VmOperationResult> StateChangedAsync(Guid vmId, VmState requested, CancellationToken ct)
    {
        var now = await _store.GetAsync(vmId, ct).ConfigureAwait(false);
        return Refuse(VmRefusalReason.StateChanged, now?.State, requested);
    }

    private static VmOperationResult.Refused Refuse(VmRefusalReason reason, VmState? current, VmState? requested, string? detail = null) =>
        new VmOperationResult.Refused(new VmRefusal(reason, current, requested, detail));
}
