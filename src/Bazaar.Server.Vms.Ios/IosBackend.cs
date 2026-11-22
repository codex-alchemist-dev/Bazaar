using Bazaar.Contracts.Modules;
using Bazaar.Server.Providers;
using Bazaar.Server.Vms.Devices;
using Bazaar.Server.Vms.Environments;
using Bazaar.Server.Vms.Lifecycle;
using Bazaar.Server.Vms.MacNode;

namespace Bazaar.Server.Vms.Ios;

/// <summary>
/// The iOS Simulator provider (doc 62 section 5.2): the client half that drives simulators on a Mac node through
/// <see cref="IMacNodeLink"/>. It implements <see cref="IVmBackend"/> so the lifecycle drives a simulator like any guest, and
/// <see cref="IDeviceControl"/> for typed app calls. Nothing runs until <see cref="StartAsync"/>; unsupported operations are never faked.
/// <para>
/// Apple rule: iOS guests run only on Apple hardware. With no Mac node linked this provider is not usable and says
/// "no Mac node linked"; it never emulates one.
/// </para>
/// The simulator is a host process on the Mac, not a virtual machine, so the capability report says <see cref="DeviceIsolation.HostProcess"/>.
/// </summary>
public sealed class IosBackend : IVmBackend, IVmProvider, ICapacityLimitedProvider, IDeviceControl, IDeviceProviderCatalogEntry, IDisposable
{
    /// <summary>The most simulators one backend will run at once, whatever the setting says.</summary>
    public const int MaxSlots = 16;

    private readonly IMacNodeLink _link;
    private readonly IosOptions _options;
    private readonly Dictionary<Guid, Instance> _instances = [];
    private readonly object _gate = new();
    private readonly SemaphoreSlim _launchGate = new(1, 1);
    private int _maxConcurrent;

    public IosBackend(IMacNodeLink link, IosOptions options)
    {
        _link = link ?? throw new ArgumentNullException(nameof(link));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        options.Validate();
        _maxConcurrent = options.MaxConcurrent;
    }

    public string Kind => IosModule.Kind;

    /// <summary>The live <c>vms.ios.max_concurrent</c> cap (doc 62 section 9.2). It starts at the configured value.</summary>
    public int MaxConcurrentInstances => Volatile.Read(ref _maxConcurrent);

    /// <summary><c>simctl</c> has no supported save call (doc 62 section 5.2).</summary>
    public bool SupportsSaveState => false;

    public DeviceProviderCapabilities Capabilities => IosModule.Capabilities with { MaxConcurrentInstances = MaxConcurrentInstances };

    public int Running => RunningGuests().Count;

    /// <summary>Applies a changed <c>vms.ios.max_concurrent</c> without a rebuild. It affects only new starts.</summary>
    public void SetMaxConcurrent(int value)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(value, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(value, MaxSlots);
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
    /// A cheap read of the link's last known state. Order of reasons: no node, node not connected, node cannot run the simulator,
    /// node runs as the owner's login or root, no runtime pinned. Starts nothing.
    /// </summary>
    public ProviderStatus Probe()
    {
        var status = _link.Status;
        if (status.UnavailableReason is { } linkReason)
        {
            return Unusable(linkReason);
        }

        var info = status.Info!;
        var issues = HostCompatibility.Check(IosModule.Requirements, info.ToHostFacts());
        if (issues.Count > 0)
        {
            return Unusable("the Mac node cannot run the iOS Simulator: " + string.Join("; ", issues.Select(i => i.Message)));
        }

        if (!info.RunsAsDedicatedUser)
        {
            return Unusable("the Mac node runs as the owner's login or as root; the iOS Simulator needs a dedicated unprivileged macOS user");
        }

        if (_options.RuntimeId.Length == 0)
        {
            return Unusable("no simulator runtime is pinned (Bazaar:Vms:Ios:RuntimeId)");
        }

        return new ProviderStatus(Kind, true, ["headless", "host-process", "remote-node"], null);
    }

    public async Task<VmHandle> CreateChildAsync(ImageVersionId parent, VmSpec spec, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(spec);
        if (spec.Os != VmOs.Ios)
        {
            throw new NotSupportedException("The iOS provider creates iOS guests only.");
        }

        if (parent.Value != _options.RuntimeVersion)
        {
            throw new InvalidOperationException($"The iOS provider accepts only the pinned simulator runtime {_options.RuntimeVersion}, not {parent.Value}.");
        }

        if (_options.RuntimeId.Length == 0)
        {
            throw new InvalidOperationException("No simulator runtime is pinned (Bazaar:Vms:Ios:RuntimeId).");
        }

        var profile = new IosDeviceProfile(_options.DeviceType, _options.RuntimeId).Validated();
        var nodeRef = await _link.CreateGuestAsync(MacGuestKind.IosSimulator, new MacGuestSpec(profile.Runtime, profile.DeviceType, 0, 0, 0), ct).ConfigureAwait(false);
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
                var max = MaxConcurrentInstances;
                if (_instances.Values.Count(i => i.State == VmState.Running) >= max)
                {
                    throw new InvalidOperationException($"The iOS provider allows {max} running simulator(s).");
                }
            }

            await _link.LaunchGuestAsync(MacGuestKind.IosSimulator, instance.NodeRef, ct).ConfigureAwait(false);
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

        await _link.StopGuestAsync(MacGuestKind.IosSimulator, instance.NodeRef, mode == StopMode.Force, ct).ConfigureAwait(false);
        Set(vm.VmId, instance with { State = VmState.Stopped });
    }

    public Task SaveStateAsync(VmHandle vm, CancellationToken ct) =>
        throw new NotSupportedException("The iOS Simulator cannot save state (simctl has no supported call).");

    public Task<CheckpointId> CheckpointAsync(VmHandle vm, CancellationToken ct) =>
        throw new NotSupportedException("The iOS Simulator cannot take checkpoints.");

    public Task RestoreAsync(VmHandle vm, CheckpointId id, CancellationToken ct) =>
        throw new NotSupportedException("The iOS Simulator cannot restore checkpoints.");

    public async Task RetireAsync(VmHandle vm, CancellationToken ct)
    {
        var instance = Find(vm);
        if (instance.State == VmState.Running)
        {
            await _link.StopGuestAsync(MacGuestKind.IosSimulator, instance.NodeRef, force: true, ct).ConfigureAwait(false);
        }

        await _link.DeleteGuestAsync(MacGuestKind.IosSimulator, instance.NodeRef, ct).ConfigureAwait(false);
        Set(vm.VmId, instance with { State = VmState.Retired });
    }

    public async Task<VmState> GetStateAsync(VmHandle vm, CancellationToken ct)
    {
        var instance = Find(vm);
        if (instance.State != VmState.Running)
        {
            return instance.State;
        }

        if (await _link.IsGuestRunningAsync(MacGuestKind.IosSimulator, instance.NodeRef, ct).ConfigureAwait(false))
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

    public async Task<DeviceScreen> CaptureAsync(Guid vmId, CancellationToken ct) =>
        await _link.ScreenshotAsync(MacGuestKind.IosSimulator, RunningRef(vmId), ct).ConfigureAwait(false);

    public Task SendInputAsync(Guid vmId, DeviceInput input, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (input is DeviceInput.Text text)
        {
            _ = DeviceIdentifiers.InputText(text.Value);
        }

        return Command(vmId, new MacGuestCommand.Input(input), ct);
    }

    public void Dispose() => _launchGate.Dispose();

    private Task Command(Guid vmId, MacGuestCommand command, CancellationToken ct) =>
        _link.RunCommandAsync(MacGuestKind.IosSimulator, RunningRef(vmId), command, ct);

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

        throw new InvalidOperationException($"VM {vmId} is not a running iOS guest.");
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

        throw new KeyNotFoundException($"No iOS guest has backend ref {vm.BackendRef}.");
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
