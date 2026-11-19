using Bazaar.Contracts.Modules;
using Bazaar.Server.Providers;
using Bazaar.Server.Vms.Devices;
using Bazaar.Server.Vms.Environments;
using Bazaar.Server.Vms.Lifecycle;

namespace Bazaar.Server.Vms.Android;

/// <summary>Resolves a workspace-relative file to a host path for one VM's workspace share (mounts module, doc 16 WP8).</summary>
public interface IAndroidWorkspace
{
    string ResolveFile(Guid vmId, string relativePath);
}

/// <summary>
/// The Android emulator provider (doc 62 section 5.1). It implements <see cref="IVmBackend"/> so the lifecycle service drives it like
/// any VM, <see cref="IVmProvider"/> for the selector and <see cref="IDeviceControl"/> for typed app calls. Nothing runs until
/// <see cref="StartAsync"/>; unsupported operations are never faked.
/// </summary>
public sealed class AndroidBackend : IVmBackend, IVmProvider, ICapacityLimitedProvider, IDeviceControl, IDisposable
{
    private readonly IAndroidHost _host;
    private readonly IAndroidHostFacts _facts;
    private readonly IAndroidWorkspace _workspace;
    private readonly AndroidOptions _options;
    private readonly Dictionary<string, Instance> _instances = new(StringComparer.Ordinal);
    private readonly Dictionary<Guid, string> _byVm = [];
    private readonly object _gate = new();
    private readonly SemaphoreSlim _launchGate = new(1, 1);

    public AndroidBackend(IAndroidHost host, IAndroidHostFacts facts, IAndroidWorkspace workspace, AndroidOptions options)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _facts = facts ?? throw new ArgumentNullException(nameof(facts));
        _workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        options.Validate();
    }

    public string Kind => AndroidModule.Kind;

    public int MaxConcurrentInstances => _options.MaxConcurrent;

    public bool SupportsSaveState => true;

    public ProviderStatus Probe()
    {
        var issues = HostCompatibility.Check(AndroidModule.Requirements, _facts.Current());
        return issues.Count > 0
            ? new ProviderStatus(Kind, false, [], string.Join("; ", issues.Select(i => i.Message)))
            : new ProviderStatus(Kind, true, ["headless", "snapshots"], null);
    }

    public async Task<VmHandle> CreateChildAsync(ImageVersionId parent, VmSpec spec, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(spec);
        if (spec.Os != VmOs.Android)
        {
            throw new NotSupportedException("The Android provider creates Android guests only.");
        }

        if (parent.Value != _options.SystemImageVersion)
        {
            throw new InvalidOperationException($"The Android provider accepts only the pinned system image {_options.SystemImageVersion}, not {parent.Value}.");
        }

        var id = Guid.CreateVersion7();
        var name = $"bz_{id:N}";
        await _host.CreateAvdAsync(name, _options.SystemImagePackage, ct).ConfigureAwait(false);
        lock (_gate)
        {
            _instances[name] = new Instance(name, null, VmState.Stopped, id);
            _byVm[id] = name;
        }

        return new VmHandle(id, name);
    }

    public async Task StartAsync(VmHandle vm, CancellationToken ct)
    {
        var instance = Find(vm);
        if (instance.State == VmState.Running)
        {
            throw new InvalidOperationException($"{vm.BackendRef} is already running.");
        }

        await StartWith(vm, instance, null, ct).ConfigureAwait(false);
    }

    public async Task StopAsync(VmHandle vm, StopMode mode, CancellationToken ct)
    {
        var instance = Find(vm);
        if (instance.State != VmState.Running || instance.Port is not { } port)
        {
            throw new InvalidOperationException($"{vm.BackendRef} is not running.");
        }

        await _host.StopEmulatorAsync(port, mode == StopMode.Force, ct).ConfigureAwait(false);
        Set(instance with { Port = null, State = VmState.Stopped });
    }

    public async Task SaveStateAsync(VmHandle vm, CancellationToken ct)
    {
        var instance = Find(vm);
        if (instance.State != VmState.Running || instance.Port is not { } port)
        {
            throw new InvalidOperationException($"{vm.BackendRef} is not running.");
        }

        await _host.SaveSnapshotAsync(port, SavedName, ct).ConfigureAwait(false);
        await _host.StopEmulatorAsync(port, force: false, ct).ConfigureAwait(false);
        Set(instance with { Port = null, State = VmState.Saved });
    }

    public async Task<CheckpointId> CheckpointAsync(VmHandle vm, CancellationToken ct)
    {
        var instance = Find(vm);
        if (instance.State is not (VmState.Stopped or VmState.Saved))
        {
            throw new InvalidOperationException("A checkpoint needs a stopped or saved VM.");
        }

        // The emulator saves a snapshot from a running guest, so boot, save and stop. A saved VM resumes from its saved state first.
        var started = await StartWith(vm, instance, instance.State == VmState.Saved ? SavedName : null, ct).ConfigureAwait(false);
        var id = new CheckpointId(Guid.CreateVersion7());
        try
        {
            await _host.SaveSnapshotAsync(started.Port!.Value, CheckpointName(id), ct).ConfigureAwait(false);
        }
        finally
        {
            await _host.StopEmulatorAsync(started.Port!.Value, force: false, ct).ConfigureAwait(false);
            Set(started with { Port = null, State = instance.State });
        }

        return id;
    }

    public Task RestoreAsync(VmHandle vm, CheckpointId id, CancellationToken ct)
    {
        var instance = Find(vm);
        if (instance.State is not (VmState.Stopped or VmState.Saved))
        {
            throw new InvalidOperationException("A restore needs a stopped or saved VM.");
        }

        return RestoreCore(vm, instance, id, ct);
    }

    public async Task RetireAsync(VmHandle vm, CancellationToken ct)
    {
        var instance = Find(vm);
        if (instance.State == VmState.Running && instance.Port is { } port)
        {
            await _host.StopEmulatorAsync(port, force: true, ct).ConfigureAwait(false);
        }

        await _host.DeleteAvdAsync(instance.AvdName, ct).ConfigureAwait(false);
        Set(instance with { Port = null, State = VmState.Retired });
    }

    public async Task<VmState> GetStateAsync(VmHandle vm, CancellationToken ct)
    {
        var instance = Find(vm);
        if (instance.State != VmState.Running || instance.Port is not { } port)
        {
            return instance.State;
        }

        if (await _host.IsRunningAsync(port, ct).ConfigureAwait(false))
        {
            return VmState.Running;
        }

        Set(instance with { Port = null, State = VmState.Stopped });
        return VmState.Stopped;
    }

    public async Task InstallAppAsync(Guid vmId, string packagePathInWorkspace, CancellationToken ct)
    {
        var port = RunningPort(vmId);
        var path = _workspace.ResolveFile(vmId, DeviceIdentifiers.WorkspaceRelativePath(packagePathInWorkspace));
        await _host.InstallAsync(port, path, ct).ConfigureAwait(false);
    }

    public Task LaunchAppAsync(Guid vmId, string appId, CancellationToken ct) =>
        _host.LaunchAsync(RunningPort(vmId), DeviceIdentifiers.AppId(appId), ct);

    public async Task<DeviceScreen> CaptureAsync(Guid vmId, CancellationToken ct) =>
        new(await _host.ScreencapAsync(RunningPort(vmId), ct).ConfigureAwait(false), DateTimeOffset.UtcNow);

    public Task SendInputAsync(Guid vmId, DeviceInput input, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);
        _ = AndroidArgv.Input(_options.BasePort, input); // validates the input before it reaches the host
        return _host.InputAsync(RunningPort(vmId), input, ct);
    }

    public void Dispose() => _launchGate.Dispose();

    private const string SavedName = "bz_saved";

    private static string CheckpointName(CheckpointId id) => $"bz_cp_{id.Value:N}";

    private async Task RestoreCore(VmHandle vm, Instance instance, CheckpointId id, CancellationToken ct)
    {
        // A restore boots from the checkpoint snapshot and leaves the VM running.
        await StartWith(vm, instance, CheckpointName(id), ct).ConfigureAwait(false);
    }

    private async Task<Instance> StartWith(VmHandle vm, Instance instance, string? snapshot, CancellationToken ct)
    {
        await _launchGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            int port;
            lock (_gate)
            {
                var used = _instances.Values.Where(i => i.Port is not null).Select(i => i.Port!.Value).ToHashSet();
                if (used.Count >= _options.MaxConcurrent)
                {
                    throw new InvalidOperationException($"The Android provider allows {_options.MaxConcurrent} running emulator(s).");
                }

                port = Enumerable.Range(0, 16).Select(n => _options.BasePort + (2 * n)).First(p => !used.Contains(p));
            }

            await _host.StartEmulatorAsync(instance.AvdName, port, snapshot, ct).ConfigureAwait(false);
            var running = instance with { Port = port, State = VmState.Running };
            Set(running);
            return running;
        }
        finally
        {
            _launchGate.Release();
        }
    }

    private int RunningPort(Guid vmId)
    {
        lock (_gate)
        {
            if (_byVm.TryGetValue(vmId, out var name) && _instances[name] is { State: VmState.Running, Port: { } port })
            {
                return port;
            }
        }

        throw new InvalidOperationException($"VM {vmId} is not a running Android guest.");
    }

    private Instance Find(VmHandle vm)
    {
        lock (_gate)
        {
            if (_instances.TryGetValue(vm.BackendRef, out var instance))
            {
                return instance;
            }
        }

        throw new KeyNotFoundException($"No Android guest has backend ref {vm.BackendRef}.");
    }

    private void Set(Instance instance)
    {
        lock (_gate)
        {
            _instances[instance.AvdName] = instance;
        }
    }

    private sealed record Instance(string AvdName, int? Port, VmState State, Guid VmId);
}
