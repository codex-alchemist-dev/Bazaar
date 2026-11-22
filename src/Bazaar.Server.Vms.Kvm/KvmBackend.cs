using Bazaar.Contracts.Modules;
using Bazaar.Server.Providers;
using Bazaar.Server.Vms.Environments;
using Bazaar.Server.Vms.Lifecycle;

namespace Bazaar.Server.Vms.Kvm;

/// <summary>
/// The KVM VM provider (doc 16). It implements <see cref="IVmBackend"/> so the lifecycle service drives it, and
/// <see cref="IVmProvider"/> so the selector can choose it for the VM role. It keeps no state of its own: the domain name is the
/// backend ref, and libvirt is the authority on what exists and what state it is in, so a server restart loses nothing.
/// </summary>
public sealed class KvmBackend : IVmBackend, IVmProvider
{
    private readonly IKvmHost _host;
    private readonly IKvmParentResolver _parents;
    private readonly IHostFactsSource _facts;

    public KvmBackend(IKvmHost host, IKvmParentResolver parents, IHostFactsSource facts)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _parents = parents ?? throw new ArgumentNullException(nameof(parents));
        _facts = facts ?? throw new ArgumentNullException(nameof(facts));
    }

    /// <inheritdoc />
    public string Kind => KvmModule.Kind;

    /// <summary>A running VM can be saved and resumed later, so a pause keeps the agent's state (kill switch containment).</summary>
    public bool SupportsSaveState => KvmModule.Capabilities.Save;

    /// <summary>Checks the host against <see cref="KvmModule.Requirements"/>. Synchronous and read-only.</summary>
    public ProviderStatus Probe()
    {
        var issues = HostCompatibility.Check(KvmModule.Requirements, _facts.Current());
        if (issues.Count > 0)
        {
            return new ProviderStatus(Kind, false, [], string.Join("; ", issues.Select(i => i.Message)));
        }

        return new ProviderStatus(Kind, true, ["save-state", "checkpoint", "linux-guests"], null);
    }

    /// <inheritdoc />
    public async Task<VmHandle> CreateChildAsync(ImageVersionId parent, VmSpec spec, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(spec);

        // A Windows guest on a Linux host has no guest transport yet (doc 16, Question 1, OPEN). Refuse before the host is touched.
        if (spec.Os == VmOs.Windows && !KvmModule.Capabilities.WindowsGuests)
        {
            throw new NotSupportedException("The KVM provider cannot create Windows guests yet: no guest transport exists for them on a Linux host.");
        }

        if (spec.Vcpu < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(spec), "A VM needs at least one virtual CPU.");
        }

        if (spec.MemoryMb < 512)
        {
            throw new ArgumentOutOfRangeException(nameof(spec), "A VM needs at least 512 MB of memory.");
        }

        var id = Guid.CreateVersion7();
        var name = KvmNames.Domain(id);
        var parentDisk = _parents.ResolveParentDisk(parent);

        var diskPath = await _host.CreateBackedDiskAsync(name, parentDisk, ct).ConfigureAwait(false);
        try
        {
            await _host.DefineAsync(new KvmDomainDefinition(name, spec.Os, spec.Vcpu, spec.MemoryMb, diskPath), ct).ConfigureAwait(false);
        }
        catch
        {
            // The disk exists but no domain owns it. Remove it so a failed create leaves nothing behind. The original error is the one to report.
            await TryRemoveAsync(name).ConfigureAwait(false);
            throw;
        }

        return new VmHandle(id, name);
    }

    /// <inheritdoc />
    public async Task StartAsync(VmHandle vm, CancellationToken ct)
    {
        await RequireAsync(vm, ct, KvmDomainState.Off, KvmDomainState.Saved).ConfigureAwait(false);
        await _host.StartAsync(vm.BackendRef, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task StopAsync(VmHandle vm, StopMode mode, CancellationToken ct)
    {
        // A force stop must also work on a guest that is mid-shutdown, crashed or suspended by its own power management:
        // those still hold memory, and a suspended guest can wake itself. Containment relies on the force stop.
        if (mode == StopMode.Force)
        {
            await RequireAsync(vm, ct, KvmDomainState.Running, KvmDomainState.Paused, KvmDomainState.Transitioning).ConfigureAwait(false);
        }
        else
        {
            await RequireAsync(vm, ct, KvmDomainState.Running, KvmDomainState.Paused).ConfigureAwait(false);
        }

        await _host.StopAsync(vm.BackendRef, mode == StopMode.Force, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task SaveStateAsync(VmHandle vm, CancellationToken ct)
    {
        await RequireAsync(vm, ct, KvmDomainState.Running, KvmDomainState.Paused).ConfigureAwait(false);
        await _host.SaveAsync(vm.BackendRef, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<CheckpointId> CheckpointAsync(VmHandle vm, CancellationToken ct)
    {
        await RequireAsync(vm, ct, KvmDomainState.Off, KvmDomainState.Saved).ConfigureAwait(false);
        var id = new CheckpointId(Guid.CreateVersion7());
        await _host.CheckpointAsync(vm.BackendRef, KvmNames.Checkpoint(id.Value), ct).ConfigureAwait(false);
        return id;
    }

    /// <inheritdoc />
    public async Task RestoreAsync(VmHandle vm, CheckpointId id, CancellationToken ct)
    {
        await RequireAsync(vm, ct, KvmDomainState.Off, KvmDomainState.Saved).ConfigureAwait(false);
        await _host.RestoreAsync(vm.BackendRef, KvmNames.Checkpoint(id.Value), ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task RetireAsync(VmHandle vm, CancellationToken ct)
    {
        EnsureName(vm);

        // A running domain is turned off first. Removing it while it runs would leave the guest process alive without a manager.
        var state = await _host.GetStateAsync(vm.BackendRef, ct).ConfigureAwait(false);
        if (state is KvmDomainState.Running or KvmDomainState.Paused or KvmDomainState.Transitioning)
        {
            await _host.StopAsync(vm.BackendRef, force: true, ct).ConfigureAwait(false);
        }

        await _host.RemoveAsync(vm.BackendRef, deleteDisk: true, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<VmState> GetStateAsync(VmHandle vm, CancellationToken ct)
    {
        EnsureName(vm);
        var state = await _host.GetStateAsync(vm.BackendRef, ct).ConfigureAwait(false);
        return state switch
        {
            KvmDomainState.Missing => VmState.Absent,
            KvmDomainState.Off => VmState.Stopped,
            KvmDomainState.Running or KvmDomainState.Paused => VmState.Running,
            KvmDomainState.Saved => VmState.Saved,
            _ => throw new InvalidOperationException($"{vm.BackendRef} is changing state; ask again when it settles."),
        };
    }

    private async Task RequireAsync(VmHandle vm, CancellationToken ct, params KvmDomainState[] allowed)
    {
        EnsureName(vm);
        var state = await _host.GetStateAsync(vm.BackendRef, ct).ConfigureAwait(false);
        if (state == KvmDomainState.Missing)
        {
            throw new KeyNotFoundException($"libvirt has no domain named {vm.BackendRef}.");
        }

        if (!allowed.Contains(state))
        {
            throw new InvalidOperationException($"{vm.BackendRef} is {state}; this operation needs {string.Join(" or ", allowed)}.");
        }
    }

    private static void EnsureName(VmHandle vm)
    {
        ArgumentNullException.ThrowIfNull(vm);
        if (!KvmNames.IsDomainName(vm.BackendRef))
        {
            throw new ArgumentException("The backend ref is not a KVM domain name this provider created.", nameof(vm));
        }
    }

    private async Task TryRemoveAsync(string name)
    {
        try
        {
            await _host.RemoveAsync(name, deleteDisk: true, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Best effort. The reconciler finds an orphan by name.
        }
    }
}
