using Bazaar.Contracts.Modules;
using Bazaar.Server.Providers;
using Bazaar.Server.Vms.Environments;
using Bazaar.Server.Vms.Lifecycle;

namespace Bazaar.Server.Vms.HyperV;

/// <summary>
/// The Hyper-V VM provider (doc 16). It implements <see cref="IVmBackend"/> so the lifecycle service drives it, and
/// <see cref="IVmProvider"/> so the selector can choose it for the VM role. It keeps no state of its own: the VM name is the
/// backend ref, and Hyper-V is the authority on what exists and what state it is in, so a server restart loses nothing.
/// </summary>
public sealed class HyperVBackend : IVmBackend, IVmProvider
{
    private readonly IHyperVHost _host;
    private readonly IHyperVParentResolver _parents;
    private readonly IHostFactsSource _facts;

    public HyperVBackend(IHyperVHost host, IHyperVParentResolver parents, IHostFactsSource facts)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _parents = parents ?? throw new ArgumentNullException(nameof(parents));
        _facts = facts ?? throw new ArgumentNullException(nameof(facts));
    }

    /// <inheritdoc />
    public string Kind => HyperVModule.Kind;

    /// <summary>A running VM can be saved and resumed later, so a pause keeps the agent's state (kill switch containment).</summary>
    public bool SupportsSaveState => HyperVModule.Capabilities.Save;

    /// <summary>Checks the host against <see cref="HyperVModule.Requirements"/>. Synchronous and read-only.</summary>
    public ProviderStatus Probe()
    {
        var issues = HostCompatibility.Check(HyperVModule.Requirements, _facts.Current());
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
        if (spec.Vcpu < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(spec), "A VM needs at least one virtual CPU.");
        }

        if (spec.MemoryMb < 512)
        {
            throw new ArgumentOutOfRangeException(nameof(spec), "A VM needs at least 512 MB of memory.");
        }

        var id = Guid.CreateVersion7();
        var name = HyperVNames.Vm(id);
        var parentDisk = _parents.ResolveParentDisk(parent);

        var diskPath = await _host.CreateDifferencingDiskAsync(name, parentDisk, ct).ConfigureAwait(false);
        try
        {
            await _host.CreateVmAsync(new HyperVVmDefinition(name, spec.Os, spec.Vcpu, spec.MemoryMb, diskPath), ct).ConfigureAwait(false);
        }
        catch
        {
            // The disk exists but no VM owns it. Remove it so a failed create leaves nothing behind. The original error is the one to report.
            await TryRemoveAsync(name).ConfigureAwait(false);
            throw;
        }

        return new VmHandle(id, name);
    }

    /// <inheritdoc />
    public async Task StartAsync(VmHandle vm, CancellationToken ct)
    {
        await RequireAsync(vm, ct, HyperVVmState.Off, HyperVVmState.Saved).ConfigureAwait(false);
        await _host.StartAsync(vm.BackendRef, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task StopAsync(VmHandle vm, StopMode mode, CancellationToken ct)
    {
        await RequireAsync(vm, ct, HyperVVmState.Running, HyperVVmState.Paused).ConfigureAwait(false);
        await _host.StopAsync(vm.BackendRef, mode == StopMode.Force, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task SaveStateAsync(VmHandle vm, CancellationToken ct)
    {
        await RequireAsync(vm, ct, HyperVVmState.Running, HyperVVmState.Paused).ConfigureAwait(false);
        await _host.SaveAsync(vm.BackendRef, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<CheckpointId> CheckpointAsync(VmHandle vm, CancellationToken ct)
    {
        await RequireAsync(vm, ct, HyperVVmState.Off, HyperVVmState.Saved).ConfigureAwait(false);
        var id = new CheckpointId(Guid.CreateVersion7());
        await _host.CheckpointAsync(vm.BackendRef, HyperVNames.Checkpoint(id.Value), ct).ConfigureAwait(false);
        return id;
    }

    /// <inheritdoc />
    public async Task RestoreAsync(VmHandle vm, CheckpointId id, CancellationToken ct)
    {
        await RequireAsync(vm, ct, HyperVVmState.Off, HyperVVmState.Saved).ConfigureAwait(false);
        await _host.RestoreAsync(vm.BackendRef, HyperVNames.Checkpoint(id.Value), ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task RetireAsync(VmHandle vm, CancellationToken ct)
    {
        EnsureName(vm);

        // A running VM is turned off first. Removing it while it runs would leave the guest process alive without a manager.
        var state = await _host.GetStateAsync(vm.BackendRef, ct).ConfigureAwait(false);
        if (state is HyperVVmState.Running or HyperVVmState.Paused)
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
            HyperVVmState.Missing => VmState.Absent,
            HyperVVmState.Off => VmState.Stopped,
            HyperVVmState.Running or HyperVVmState.Paused => VmState.Running,
            HyperVVmState.Saved => VmState.Saved,
            _ => throw new InvalidOperationException($"{vm.BackendRef} is changing state; ask again when it settles."),
        };
    }

    private async Task RequireAsync(VmHandle vm, CancellationToken ct, params HyperVVmState[] allowed)
    {
        EnsureName(vm);
        var state = await _host.GetStateAsync(vm.BackendRef, ct).ConfigureAwait(false);
        if (state == HyperVVmState.Missing)
        {
            throw new KeyNotFoundException($"Hyper-V has no VM named {vm.BackendRef}.");
        }

        if (!allowed.Contains(state))
        {
            throw new InvalidOperationException($"{vm.BackendRef} is {state}; this operation needs {string.Join(" or ", allowed)}.");
        }

    }

    private static void EnsureName(VmHandle vm)
    {
        ArgumentNullException.ThrowIfNull(vm);
        if (!HyperVNames.IsVmName(vm.BackendRef))
        {
            throw new ArgumentException("The backend ref is not a Hyper-V VM name this provider created.", nameof(vm));
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
