using Bazaar.Contracts.Modules;
using Bazaar.Server.Providers;
using Bazaar.Server.Vms.Environments;
using Bazaar.Server.Vms.Lifecycle;

namespace Bazaar.Server.Vms.MicroVm;

/// <summary>
/// The microVM VM provider (doc 62 section 5.0): many small Linux guests on KVM. It implements <see cref="IVmBackend"/> and
/// <see cref="IVmProvider"/>. Saved state and checkpoints throw <see cref="NotSupportedException"/> until the spike proves them;
/// nothing is faked. Nothing runs until <see cref="StartAsync"/>.
/// </summary>
public sealed class MicroVmBackend : IVmBackend, IVmProvider, ICapacityLimitedProvider, IDisposable
{
    private readonly IMicroVmMonitor _monitor;
    private readonly IMicroVmHostFacts _facts;
    private readonly MicroVmOptions _options;
    private readonly Dictionary<string, VmState> _states = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private readonly SemaphoreSlim _launchGate = new(1, 1);

    public MicroVmBackend(IMicroVmMonitor monitor, IMicroVmHostFacts facts, MicroVmOptions options)
    {
        _monitor = monitor ?? throw new ArgumentNullException(nameof(monitor));
        _facts = facts ?? throw new ArgumentNullException(nameof(facts));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        options.Validate();
    }

    public string Kind => MicroVmModule.Kind;

    public int MaxConcurrentInstances => _options.MaxConcurrent;

    public ProviderStatus Probe()
    {
        var issues = HostCompatibility.Check(MicroVmModule.Requirements, _facts.Current());
        return issues.Count > 0
            ? new ProviderStatus(Kind, false, [], string.Join("; ", issues.Select(i => i.Message)))
            : new ProviderStatus(Kind, true, ["headless", "linux-only"], null);
    }

    public async Task<VmHandle> CreateChildAsync(ImageVersionId parent, VmSpec spec, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(spec);
        if (spec.Os != VmOs.Linux)
        {
            throw new NotSupportedException("The microVM provider runs Linux guests only.");
        }

        if (parent.Value != _options.ImageVersion)
        {
            throw new InvalidOperationException($"The microVM provider accepts only the pinned image {_options.ImageVersion}, not {parent.Value}.");
        }

        var id = Guid.CreateVersion7();
        var name = $"mv{id:N}";
        await _monitor.CreateDiskAsync(name, spec.Vcpu, spec.MemoryMb, spec.DiskCapGb, ct).ConfigureAwait(false);
        lock (_gate)
        {
            _states[name] = VmState.Stopped;
        }

        return new VmHandle(id, name);
    }

    public async Task StartAsync(VmHandle vm, CancellationToken ct)
    {
        if (State(vm) == VmState.Running)
        {
            throw new InvalidOperationException($"{vm.BackendRef} is already running.");
        }

        await _launchGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            lock (_gate)
            {
                if (_states.Values.Count(s => s == VmState.Running) >= _options.MaxConcurrent)
                {
                    throw new InvalidOperationException($"The microVM provider allows {_options.MaxConcurrent} running VMs.");
                }
            }

            await _monitor.BootAsync(vm.BackendRef, ct).ConfigureAwait(false);
            Set(vm, VmState.Running);
        }
        finally
        {
            _launchGate.Release();
        }
    }

    public async Task StopAsync(VmHandle vm, StopMode mode, CancellationToken ct)
    {
        if (State(vm) != VmState.Running)
        {
            throw new InvalidOperationException($"{vm.BackendRef} is not running.");
        }

        await _monitor.StopAsync(vm.BackendRef, mode == StopMode.Force, ct).ConfigureAwait(false);
        Set(vm, VmState.Stopped);
    }

    public Task SaveStateAsync(VmHandle vm, CancellationToken ct) => throw Unsupported("save state");

    public Task<CheckpointId> CheckpointAsync(VmHandle vm, CancellationToken ct) => throw Unsupported("checkpoint");

    public Task RestoreAsync(VmHandle vm, CheckpointId id, CancellationToken ct) => throw Unsupported("restore");

    public async Task RetireAsync(VmHandle vm, CancellationToken ct)
    {
        if (State(vm) == VmState.Running)
        {
            await _monitor.StopAsync(vm.BackendRef, force: true, ct).ConfigureAwait(false);
        }

        await _monitor.DeleteAsync(vm.BackendRef, ct).ConfigureAwait(false);
        Set(vm, VmState.Retired);
    }

    public async Task<VmState> GetStateAsync(VmHandle vm, CancellationToken ct)
    {
        var state = State(vm);
        if (state != VmState.Running || await _monitor.IsRunningAsync(vm.BackendRef, ct).ConfigureAwait(false))
        {
            return state;
        }

        Set(vm, VmState.Stopped);
        return VmState.Stopped;
    }

    public void Dispose() => _launchGate.Dispose();

    private static NotSupportedException Unsupported(string operation) =>
        new($"The microVM provider does not support {operation} yet (doc 62, spike S-MVM).");

    private VmState State(VmHandle vm)
    {
        lock (_gate)
        {
            return _states.TryGetValue(vm.BackendRef, out var s) ? s : throw new KeyNotFoundException($"No microVM has backend ref {vm.BackendRef}.");
        }
    }

    private void Set(VmHandle vm, VmState state)
    {
        lock (_gate)
        {
            _states[vm.BackendRef] = state;
        }
    }
}
