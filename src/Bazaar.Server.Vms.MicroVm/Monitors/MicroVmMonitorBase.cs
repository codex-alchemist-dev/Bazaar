using System.Text.RegularExpressions;

namespace Bazaar.Server.Vms.MicroVm.Monitors;

/// <summary>Shared lifecycle of the real monitor adapters: validated names and paths, copy-on-write disks, launch, graceful stop then kill.</summary>
public abstract partial class MicroVmMonitorBase : IMicroVmMonitor
{
    private const int _firstGuestCid = 3;
    private const int _connectAttempts = 50;
    private readonly Dictionary<string, VmRuntime> _vms = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private readonly IMicroVmApiClient _api;
    private readonly IMicroVmProcessLauncher _launcher;
    private readonly IMicroVmTapNetwork _network;
    private readonly IMicroVmDiskCloner _cloner;
    private readonly TimeSpan _stopTimeout;
    private readonly TimeSpan _connectDelay;
    private readonly string _stateRoot;
    private int _cidCounter = _firstGuestCid - 1;

    protected MicroVmMonitorBase(
        MicroVmOptions options,
        IMicroVmApiClient api,
        IMicroVmProcessLauncher launcher,
        IMicroVmTapNetwork network,
        IMicroVmDiskCloner? cloner = null,
        TimeSpan? stopTimeout = null,
        TimeSpan? connectDelay = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        Options = options;
        if (options.MonitorBinaryPath is null || options.KernelPath is null || options.RootfsPath is null)
        {
            throw new InvalidOperationException("The real microVM monitor needs MonitorBinaryPath, KernelPath and RootfsPath.");
        }

        _api = api ?? throw new ArgumentNullException(nameof(api));
        _launcher = launcher ?? throw new ArgumentNullException(nameof(launcher));
        _network = network ?? throw new ArgumentNullException(nameof(network));
        _cloner = cloner ?? new NoReflinkDiskCloner();
        _stopTimeout = stopTimeout ?? TimeSpan.FromSeconds(10);
        _connectDelay = connectDelay ?? TimeSpan.FromMilliseconds(100);
        _stateRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(options.StateDirectory));
    }

    protected MicroVmOptions Options { get; }

    /// <summary>Where the monitor's files for one VM live on the host and how the monitor itself names them.</summary>
    protected sealed record VmPaths(string HostDisk, string HostSocket, string HostVsock, string ApiDisk, string ApiKernel, string ApiSocket, string ApiVsock);

    /// <summary>Everything one VM needs after <see cref="CreateDiskAsync"/>.</summary>
    protected sealed record VmConfig(string Name, int Vcpu, int MemoryMb, uint Cid, string Tap, VmPaths Paths);

    /// <summary>Checks a VM name against the one accepted shape, <c>mv</c> plus 32 hex digits.</summary>
    public static bool IsValidName(string? name) => name is not null && NameRegex().IsMatch(name);

    /// <summary>The tap device name of a VM (15 characters at most, from the random tail of the name).</summary>
    public static string TapName(string name)
    {
        RequireName(name);
        return "tap" + name[^12..];
    }

    public async Task CreateDiskAsync(string name, int vcpu, int memoryMb, int diskCapGb, CancellationToken ct)
    {
        RequireName(name);
        ArgumentOutOfRangeException.ThrowIfLessThan(vcpu, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(memoryMb, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(diskCapGb, 1);
        var paths = PathsFor(name);
        var dir = Path.GetDirectoryName(paths.HostDisk)!;
        Directory.CreateDirectory(dir);
        PrepareDirectories(name);
        var cid = (uint)Interlocked.Increment(ref _cidCounter);
        var config = new VmConfig(name, vcpu, memoryMb, cid, TapName(name), paths);
        lock (_gate)
        {
            if (!_vms.TryAdd(name, new VmRuntime(config)))
            {
                throw new InvalidOperationException($"{name} already has a disk.");
            }
        }

        try
        {
            var rootfs = Options.RootfsPath!;
            var cap = (long)diskCapGb * 1024 * 1024 * 1024;
            if (new FileInfo(rootfs).Length > cap)
            {
                throw new InvalidOperationException("The parent root file system is larger than the disk cap.");
            }

            if (_cloner.ReflinkSupported(dir))
            {
                await _cloner.ReflinkAsync(rootfs, paths.HostDisk, ct).ConfigureAwait(false);
            }
            else
            {
                File.Copy(rootfs, paths.HostDisk, overwrite: false);
            }

            await using (var fs = new FileStream(paths.HostDisk, FileMode.Open, FileAccess.Write))
            {
                if (fs.Length < cap)
                {
                    fs.SetLength(cap);
                }
            }

            StageKernel(name);
        }
        catch
        {
            lock (_gate)
            {
                _vms.Remove(name);
            }

            RemoveFiles(name);
            throw;
        }
    }

    public async Task BootAsync(string name, CancellationToken ct)
    {
        VmRuntime vm = Get(name);
        if (vm.Process is { HasExited: false })
        {
            throw new InvalidOperationException($"{name} is already running.");
        }

        var cfg = vm.Config;
        await _network.CreateAsync(cfg.Tap, ct).ConfigureAwait(false);
        vm.TapCreated = true;
        try
        {
            File.Delete(cfg.Paths.HostSocket);
            vm.Process = _launcher.Launch(BuildLaunchArgv(cfg));
            var first = true;
            foreach (var request in BuildBootRequests(cfg))
            {
                await SendCheckedAsync(cfg, request, first, vm, ct).ConfigureAwait(false);
                first = false;
            }
        }
        catch
        {
            KillProcess(vm);
            await DeleteTapAsync(vm, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    public async Task StopAsync(string name, bool force, CancellationToken ct)
    {
        VmRuntime vm = Get(name);
        if (vm.Process is null || vm.Process.HasExited)
        {
            return;
        }

        if (!force)
        {
            try
            {
                await SendCheckedAsync(vm.Config, BuildGracefulStopRequest(), ct).ConfigureAwait(false);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(_stopTimeout);
                await vm.Process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // Graceful stop timed out: fall through to kill.
            }
            catch (HttpRequestException)
            {
                // The monitor did not answer: fall through to kill.
            }
        }

        KillProcess(vm);
        await DeleteTapAsync(vm, ct).ConfigureAwait(false);
    }

    public Task<bool> IsRunningAsync(string name, CancellationToken ct)
    {
        lock (_gate)
        {
            return Task.FromResult(_vms.TryGetValue(name ?? string.Empty, out var vm) && vm.Process is { HasExited: false });
        }
    }

    public async Task DeleteAsync(string name, CancellationToken ct)
    {
        VmRuntime vm = Get(name);
        KillProcess(vm);
        lock (_gate)
        {
            _vms.Remove(name);
        }

        RemoveFiles(name);
        await DeleteTapAsync(vm, ct).ConfigureAwait(false);
    }

    /// <summary>The argument list that starts the monitor for one VM.</summary>
    protected abstract IReadOnlyList<string> BuildLaunchArgv(VmConfig config);

    /// <summary>The ordered API calls that configure and start the guest.</summary>
    protected abstract IReadOnlyList<MicroVmApiRequest> BuildBootRequests(VmConfig config);

    /// <summary>The API call that asks the guest to power off.</summary>
    protected abstract MicroVmApiRequest BuildGracefulStopRequest();

    /// <summary>Host paths and monitor-visible paths of one VM. The name has already been validated.</summary>
    protected virtual VmPaths PathsFor(string name)
    {
        var disk = Inside(Path.Combine(_stateRoot, name + ".ext4"));
        var socket = Inside(Path.Combine(_stateRoot, name + ".sock"));
        var vsock = Inside(Path.Combine(_stateRoot, name + ".vsock"));
        return new VmPaths(disk, socket, vsock, disk, Options.KernelPath!, socket, vsock);
    }

    /// <summary>Hook to create extra per-VM directories (for example a jail root).</summary>
    protected virtual void PrepareDirectories(string name)
    {
    }

    /// <summary>Hook to place the kernel where the monitor can see it.</summary>
    protected virtual void StageKernel(string name)
    {
    }

    /// <summary>Files and directories to delete when a VM is retired. Every entry is inside the state directory.</summary>
    protected virtual IEnumerable<string> RemovablePaths(string name)
    {
        var p = PathsFor(name);
        return [p.HostDisk, p.HostSocket, p.HostVsock];
    }

    /// <summary>Returns the full path only if it is inside the state directory; otherwise throws.</summary>
    protected string Inside(string path)
    {
        var full = Path.GetFullPath(path);
        if (!full.StartsWith(_stateRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The path is outside the microVM state directory.");
        }

        return full;
    }

    /// <summary>Throws unless the name is <c>mv</c> plus 32 lower-case hex digits.</summary>
    protected static void RequireName(string? name)
    {
        if (!IsValidName(name))
        {
            throw new ArgumentException("A microVM name must match ^mv[0-9a-f]{32}$.", nameof(name));
        }
    }

    [GeneratedRegex("^mv[0-9a-f]{32}$", RegexOptions.CultureInvariant)]
    private static partial Regex NameRegex();

    private static void KillProcess(VmRuntime vm)
    {
        if (vm.Process is null)
        {
            return;
        }

        if (!vm.Process.HasExited)
        {
            vm.Process.Kill();
        }

        vm.Process.Dispose();
        vm.Process = null;
    }

    private VmRuntime Get(string name)
    {
        RequireName(name);
        lock (_gate)
        {
            return _vms.TryGetValue(name, out var vm) ? vm : throw new KeyNotFoundException($"No microVM named {name}.");
        }
    }

    private void RemoveFiles(string name)
    {
        foreach (var path in RemovablePaths(name))
        {
            var full = Inside(path);
            if (Directory.Exists(full))
            {
                Directory.Delete(full, recursive: true);
            }
            else
            {
                File.Delete(full);
            }
        }
    }

    private async Task DeleteTapAsync(VmRuntime vm, CancellationToken ct)
    {
        if (vm.TapCreated)
        {
            vm.TapCreated = false;
            await _network.DeleteAsync(vm.Config.Tap, ct).ConfigureAwait(false);
        }
    }

    private Task SendCheckedAsync(VmConfig cfg, MicroVmApiRequest request, CancellationToken ct) =>
        SendCheckedAsync(cfg, request, false, null, ct);

    private async Task SendCheckedAsync(VmConfig cfg, MicroVmApiRequest request, bool retryUntilUp, VmRuntime? vm, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                var response = await _api.SendAsync(cfg.Paths.HostSocket, request, ct).ConfigureAwait(false);
                if (!response.IsSuccess)
                {
                    throw new InvalidOperationException($"{request.Method} {request.Path} failed with status {response.StatusCode}.");
                }

                return;
            }
            catch (Exception e) when (retryUntilUp && attempt < _connectAttempts && e is HttpRequestException or IOException or System.Net.Sockets.SocketException)
            {
                if (vm?.Process is null or { HasExited: true })
                {
                    throw new InvalidOperationException("The monitor exited before its API socket came up.", e);
                }

                await Task.Delay(_connectDelay, ct).ConfigureAwait(false);
            }
        }
    }

    private sealed class VmRuntime(VmConfig config)
    {
        public VmConfig Config { get; } = config;

        public IMicroVmProcess? Process { get; set; }

        public bool TapCreated { get; set; }
    }
}
