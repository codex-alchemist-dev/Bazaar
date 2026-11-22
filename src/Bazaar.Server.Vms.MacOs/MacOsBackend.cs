using Bazaar.Contracts.Modules;
using Bazaar.Server.Providers;
using Bazaar.Server.Vms.Devices;
using Bazaar.Server.Vms.Environments;
using Bazaar.Server.Vms.Lifecycle;
using Bazaar.Server.Vms.MacNode;

namespace Bazaar.Server.Vms.MacOs;

/// <summary>
/// The macOS VM provider (doc 62 section 5.3): the client half that drives Virtualization.framework guests on a Mac node through
/// <see cref="IMacNodeLink"/>. It implements <see cref="IVmBackend"/> so the lifecycle drives a guest like any VM, and
/// <see cref="IDeviceControl"/> for the typed app calls the guest agent serves. Nothing runs until <see cref="StartAsync"/>.
/// <para>
/// Apple rule: macOS guests run only on Apple hardware, with at most two virtual instances per Mac. The cap here is never above
/// <see cref="MacOsInstanceCap.HardCap"/>, and never above what the node itself reports. With no Mac node linked this provider is not
/// usable and says "no Mac node linked".
/// </para>
/// Screen capture and input are not offered until spike S-MAC passes (the report says <c>ScreenSource = false</c>), and saved state is
/// not offered either. Unsupported operations throw <see cref="NotSupportedException"/> and the node is not called.
/// </summary>
public sealed class MacOsBackend : IVmBackend, IVmProvider, ICapacityLimitedProvider, IDeviceControl, IDeviceProviderCatalogEntry, IDisposable
{
    private readonly IMacNodeLink _link;
    private readonly MacOsOptions _options;
    private readonly Dictionary<Guid, Instance> _instances = [];
    private readonly object _gate = new();
    private readonly SemaphoreSlim _launchGate = new(1, 1);
    private int _maxConcurrent;

    public MacOsBackend(IMacNodeLink link, MacOsOptions options)
    {
        _link = link ?? throw new ArgumentNullException(nameof(link));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        options.Validate();
        _maxConcurrent = options.MaxConcurrent;
    }

    public string Kind => MacOsModule.Kind;

    /// <summary>The live <c>vms.macos.max_concurrent</c> cap, never above Apple's two (doc 62 sections 3 and 9.2).</summary>
    public int MaxConcurrentInstances => Volatile.Read(ref _maxConcurrent);

    /// <summary>Saved machine state is unverified for macOS guests (doc 62 section 5.3), so it is not offered.</summary>
    public bool SupportsSaveState => false;

    public DeviceProviderCapabilities Capabilities => MacOsModule.Capabilities with { MaxConcurrentInstances = MaxConcurrentInstances };

    public int Running => RunningGuests().Count;

    /// <summary>Applies a changed <c>vms.macos.max_concurrent</c> without a rebuild. It affects only new starts. Above two is refused.</summary>
    public void SetMaxConcurrent(int value)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(value, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(value, MacOsInstanceCap.HardCap);
        Volatile.Write(ref _maxConcurrent, value);
    }

    public DeviceProbeResult ProbeDevice()
    {
        var status = Probe();
        return new DeviceProbeResult(status.Usable, status.Usable ? null : status.Reason);
    }

    public IReadOnlyList<DeviceGuestState> RunningGuests()
    {
        lock (_gate)
        {
            return _instances
                .Where(kv => kv.Value.State == VmState.Running)
                .OrderBy(kv => kv.Key)
                .Select(kv => new DeviceGuestState(kv.Key, kv.Value.State.ToString()))
                .ToArray();
        }
    }

    /// <summary>
    /// A cheap read of the link's last known state. Order of reasons: no node, node not connected, node is not an Apple silicon Mac with
    /// the virtualization framework, no restore image pinned. Starts nothing.
    /// </summary>
    public ProviderStatus Probe()
    {
        var status = _link.Status;
        if (status.UnavailableReason is { } linkReason)
        {
            return Unusable(linkReason);
        }

        var issues = HostCompatibility.Check(MacOsModule.Requirements, status.Info!.ToHostFacts());
        if (issues.Count > 0)
        {
            return Unusable("the Mac node cannot run macOS guests: " + string.Join("; ", issues.Select(i => i.Message)));
        }

        if (!_options.IsPinned)
        {
            return Unusable("no macOS restore image is pinned (vms.macos.restore_image_pin)");
        }

        return new ProviderStatus(Kind, true, ["headless", "guest-agent", "remote-node"], null);
    }

    public async Task<VmHandle> CreateChildAsync(ImageVersionId parent, VmSpec spec, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(spec);
        if (spec.Os != VmOs.MacOs)
        {
            throw new NotSupportedException("The macOS provider creates macOS guests only.");
        }

        if (parent.Value != _options.RestoreImageVersion)
        {
            throw new InvalidOperationException($"The macOS provider accepts only the pinned restore image {_options.RestoreImageVersion}, not {parent.Value}.");
        }

        if (!_options.IsPinned)
        {
            throw new InvalidOperationException("No macOS restore image is pinned (vms.macos.restore_image_pin).");
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(spec.Vcpu, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(spec.MemoryMb, 1024);
        ArgumentOutOfRangeException.ThrowIfLessThan(spec.DiskCapGb, 1);
        var nodeRef = await _link.CreateGuestAsync(
            MacGuestKind.MacOsVm,
            new MacGuestSpec(_options.RestoreImageId, null, spec.Vcpu, spec.MemoryMb, spec.DiskCapGb),
            ct).ConfigureAwait(false);
        var id = Guid.CreateVersion7();
        lock (_gate)
        {
            _instances[id] = new Instance(nodeRef, VmState.Stopped);
        }

        return new VmHandle(id, nodeRef);
    }

    public async Task StartAsync(VmHandle vm, CancellationToken ct)
    {
        var instance = Find(vm);
        if (instance.State == VmState.Running)
        {
            throw new InvalidOperationException($"{vm.BackendRef} is already running.");
        }

        await _launchGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            lock (_gate)
            {
                // Apple's cap first, then the operator's setting, then what the node itself allows.
                var max = Math.Min(MacOsInstanceCap.Effective(MaxConcurrentInstances), Math.Max(1, _link.Status.Info?.MaxVmInstances ?? MacOsInstanceCap.HardCap));
                if (_instances.Values.Count(i => i.State == VmState.Running) >= max)
                {
                    throw new InvalidOperationException($"The macOS provider allows {max} running virtual machine(s) (Apple's limit is {MacOsInstanceCap.HardCap} per Mac).");
                }
            }

            await _link.LaunchGuestAsync(MacGuestKind.MacOsVm, instance.NodeRef, ct).ConfigureAwait(false);
            Set(vm.VmId, instance with { State = VmState.Running });
        }
        finally
        {
            _launchGate.Release();
        }
    }

    public async Task StopAsync(VmHandle vm, StopMode mode, CancellationToken ct)
    {
        var instance = Find(vm);
        if (instance.State != VmState.Running)
        {
            throw new InvalidOperationException($"{vm.BackendRef} is not running.");
        }

        await _link.StopGuestAsync(MacGuestKind.MacOsVm, instance.NodeRef, mode == StopMode.Force, ct).ConfigureAwait(false);
        Set(vm.VmId, instance with { State = VmState.Stopped });
    }

    public Task SaveStateAsync(VmHandle vm, CancellationToken ct) =>
        throw new NotSupportedException("Saved state for macOS guests is not offered until spike S-MAC confirms it.");

    public Task<CheckpointId> CheckpointAsync(VmHandle vm, CancellationToken ct) =>
        throw new NotSupportedException("Checkpoints for macOS guests are not offered until spike S-MAC confirms them.");

    public Task RestoreAsync(VmHandle vm, CheckpointId id, CancellationToken ct) =>
        throw new NotSupportedException("Restoring macOS guests is not offered until spike S-MAC confirms it.");

    public async Task RetireAsync(VmHandle vm, CancellationToken ct)
    {
        var instance = Find(vm);
        if (instance.State == VmState.Running)
        {
            await _link.StopGuestAsync(MacGuestKind.MacOsVm, instance.NodeRef, force: true, ct).ConfigureAwait(false);
        }

        await _link.DeleteGuestAsync(MacGuestKind.MacOsVm, instance.NodeRef, ct).ConfigureAwait(false);
        Set(vm.VmId, instance with { State = VmState.Retired });
    }

    public async Task<VmState> GetStateAsync(VmHandle vm, CancellationToken ct)
    {
        var instance = Find(vm);
        if (instance.State != VmState.Running)
        {
            return instance.State;
        }

        if (await _link.IsGuestRunningAsync(MacGuestKind.MacOsVm, instance.NodeRef, ct).ConfigureAwait(false))
        {
            return VmState.Running;
        }

        Set(vm.VmId, instance with { State = VmState.Stopped });
        return VmState.Stopped;
    }

    public Task InstallAppAsync(Guid vmId, string packagePathInWorkspace, CancellationToken ct) =>
        Command(vmId, new MacGuestCommand.InstallApp(DeviceIdentifiers.WorkspaceRelativePath(packagePathInWorkspace)), ct);

    public Task LaunchAppAsync(Guid vmId, string appId, CancellationToken ct) =>
        Command(vmId, new MacGuestCommand.LaunchApp(DeviceIdentifiers.AppId(appId)), ct);

    public Task<DeviceScreen> CaptureAsync(Guid vmId, CancellationToken ct) =>
        throw new NotSupportedException("Screen capture for macOS guests is not offered until spike S-MAC passes (ScreenSource is false).");

    public Task SendInputAsync(Guid vmId, DeviceInput input, CancellationToken ct) =>
        throw new NotSupportedException("Input for macOS guests is not offered until spike S-MAC passes (ScreenSource is false).");

    public void Dispose() => _launchGate.Dispose();

    private Task Command(Guid vmId, MacGuestCommand command, CancellationToken ct) =>
        _link.RunCommandAsync(MacGuestKind.MacOsVm, RunningRef(vmId), command, ct);

    private ProviderStatus Unusable(string reason) => new(Kind, false, [], reason);

    private string RunningRef(Guid vmId)
    {
        lock (_gate)
        {
            if (_instances.TryGetValue(vmId, out var instance) && instance.State == VmState.Running)
            {
                return instance.NodeRef;
            }
        }

        throw new InvalidOperationException($"VM {vmId} is not a running macOS guest.");
    }

    private Instance Find(VmHandle vm)
    {
        lock (_gate)
        {
            if (_instances.TryGetValue(vm.VmId, out var instance) && string.Equals(instance.NodeRef, vm.BackendRef, StringComparison.Ordinal))
            {
                return instance;
            }
        }

        throw new KeyNotFoundException($"No macOS guest has backend ref {vm.BackendRef}.");
    }

    private void Set(Guid vmId, Instance instance)
    {
        lock (_gate)
        {
            _instances[vmId] = instance;
        }
    }

    private sealed record Instance(string NodeRef, VmState State);
}
