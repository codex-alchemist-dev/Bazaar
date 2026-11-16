using Bazaar.Contracts.Modules;
using Bazaar.Server.Providers;
using Bazaar.Server.Vms.Environments;
using Bazaar.Server.Vms.Lifecycle;

namespace Bazaar.Server.Vms.WindowsSandbox;

/// <summary>
/// The Windows Sandbox VM provider (doc 50). It implements <see cref="IVmBackend"/> so the lifecycle service drives it like Hyper-V,
/// and <see cref="IVmProvider"/> so the selector can choose it for the VM role. Operations Windows Sandbox cannot do throw
/// <see cref="NotSupportedException"/>, matching <see cref="WindowsSandboxModule.Capabilities"/>. Saved state and checkpoints are never faked.
/// </summary>
public sealed class WindowsSandboxBackend : IVmBackend, IVmProvider, IDisposable
{
    private readonly IWindowsSandboxHost _host;
    private readonly IHostFactsSource _facts;
    private readonly WindowsSandboxOptions _options;
    private readonly Dictionary<string, Instance> _instances = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    // Serialises the capacity check and the launch, so two starts cannot both see zero running sandboxes.
    private readonly SemaphoreSlim _launchGate = new(1, 1);

    public WindowsSandboxBackend(IWindowsSandboxHost host, IHostFactsSource facts, WindowsSandboxOptions options)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _facts = facts ?? throw new ArgumentNullException(nameof(facts));
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    /// <inheritdoc />
    public string Kind => WindowsSandboxModule.Kind;

    /// <summary>
    /// Checks the host against <see cref="WindowsSandboxModule.Requirements"/>. Synchronous and read-only.
    /// </summary>
    public ProviderStatus Probe()
    {
        var issues = HostCompatibility.Check(WindowsSandboxModule.Requirements, _facts.Current());
        if (issues.Count > 0)
        {
            return new ProviderStatus(Kind, false, [], string.Join("; ", issues.Select(i => i.Message)));
        }

        return new ProviderStatus(Kind, true, ["single-instance", "ephemeral"], null);
    }

    /// <inheritdoc />
    public async Task<VmHandle> CreateChildAsync(ImageVersionId parent, VmSpec spec, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(spec);

        if (spec.Os != VmOs.Windows)
        {
            throw new NotSupportedException("Windows Sandbox runs Windows guests only; a Linux VM needs another provider.");
        }

        if (parent.Value != _options.HostBaseImage)
        {
            throw new InvalidOperationException(
                $"Windows Sandbox accepts only the configured host base image {_options.HostBaseImage}, not {parent.Value}.");
        }

        var id = Guid.CreateVersion7();
        var backendRef = $"wsb-{id:N}";
        var configPath = await _host.WriteConfigAsync(backendRef, WindowsSandboxConfig.Build(spec, _options.Networking), ct).ConfigureAwait(false);

        lock (_gate)
        {
            _instances[backendRef] = new Instance(configPath, null, VmState.Stopped);
        }

        return new VmHandle(id, backendRef);
    }

    /// <inheritdoc />
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
            // The host is the authority: a sandbox started outside this server also counts against the single-instance limit.
            var running = await _host.CountRunningAsync(ct).ConfigureAwait(false);
            if (running >= WindowsSandboxModule.Capabilities.MaxConcurrentInstances)
            {
                throw new InvalidOperationException(
                    $"Windows Sandbox allows {WindowsSandboxModule.Capabilities.MaxConcurrentInstances} running instance per host and {running} is running now.");
            }

            var sandboxId = await _host.LaunchAsync(instance.ConfigPath, ct).ConfigureAwait(false);
            lock (_gate)
            {
                _instances[vm.BackendRef] = instance with { SandboxId = sandboxId, State = VmState.Running };
            }
        }
        finally
        {
            _launchGate.Release();
        }
    }

    /// <inheritdoc />
    public async Task StopAsync(VmHandle vm, StopMode mode, CancellationToken ct)
    {
        var instance = Find(vm);
        if (instance.State != VmState.Running || instance.SandboxId is null)
        {
            throw new InvalidOperationException($"{vm.BackendRef} is not running.");
        }

        await _host.ShutdownAsync(instance.SandboxId, mode == StopMode.Force, ct).ConfigureAwait(false);
        lock (_gate)
        {
            _instances[vm.BackendRef] = instance with { SandboxId = null, State = VmState.Stopped };
        }
    }

    /// <summary>Not supported: a sandbox's writable state is discarded when it closes, so there is nothing to save.</summary>
    public Task SaveStateAsync(VmHandle vm, CancellationToken ct) =>
        throw Unsupported("save state");

    /// <summary>Not supported: no checkpoints. The lifecycle service must not reach this path (see doc 50 section 4).</summary>
    public Task<CheckpointId> CheckpointAsync(VmHandle vm, CancellationToken ct) =>
        throw Unsupported("checkpoint");

    /// <summary>Not supported: no checkpoints to restore.</summary>
    public Task RestoreAsync(VmHandle vm, CheckpointId id, CancellationToken ct) =>
        throw Unsupported("restore");

    /// <inheritdoc />
    public async Task RetireAsync(VmHandle vm, CancellationToken ct)
    {
        var instance = Find(vm);
        if (instance.SandboxId is not null && instance.State == VmState.Running)
        {
            await _host.ShutdownAsync(instance.SandboxId, force: true, ct).ConfigureAwait(false);
        }

        await _host.DeleteConfigAsync(instance.ConfigPath, ct).ConfigureAwait(false);
        lock (_gate)
        {
            _instances[vm.BackendRef] = instance with { SandboxId = null, State = VmState.Retired };
        }
    }

    /// <summary>
    /// Reports the state. A running sandbox that the host no longer reports (closed by a user, or after a host restart) is recorded as stopped.
    /// </summary>
    public async Task<VmState> GetStateAsync(VmHandle vm, CancellationToken ct)
    {
        var instance = Find(vm);
        if (instance.State != VmState.Running || instance.SandboxId is null)
        {
            return instance.State;
        }

        if (await _host.IsRunningAsync(instance.SandboxId, ct).ConfigureAwait(false))
        {
            return VmState.Running;
        }

        lock (_gate)
        {
            _instances[vm.BackendRef] = instance with { SandboxId = null, State = VmState.Stopped };
        }

        return VmState.Stopped;
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

        throw new KeyNotFoundException($"No Windows Sandbox instance has backend ref {vm.BackendRef}.");
    }

    /// <inheritdoc />
    public void Dispose() => _launchGate.Dispose();

    private static NotSupportedException Unsupported(string operation) =>
        new($"Windows Sandbox does not support {operation}: instances are discarded on close.");

    /// <summary>Per-instance bookkeeping. Immutable: every change replaces the record.</summary>
    private sealed record Instance(string ConfigPath, string? SandboxId, VmState State);
}
