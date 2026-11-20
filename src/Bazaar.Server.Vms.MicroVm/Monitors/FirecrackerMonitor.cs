namespace Bazaar.Server.Vms.MicroVm.Monitors;

/// <summary>
/// Firecracker adapter: launches <c>firecracker --api-sock</c> (or the jailer when <see cref="MicroVmOptions.JailerPath"/> is set)
/// and configures the guest over its REST API. Graceful stop is SendCtrlAltDel; the process is killed after the stop timeout.
/// </summary>
public sealed class FirecrackerMonitor(
    MicroVmOptions options,
    IMicroVmApiClient api,
    IMicroVmProcessLauncher launcher,
    IMicroVmTapNetwork network,
    IMicroVmDiskCloner? cloner = null,
    TimeSpan? stopTimeout = null,
    TimeSpan? connectDelay = null)
    : MicroVmMonitorBase(options, api, launcher, network, cloner, stopTimeout, connectDelay)
{
    private const string _inJailSocket = "/run/firecracker.socket";
    private const string _inJailKernel = "/vmlinux";
    private const string _inJailVsock = "/v.sock";
    private const string _inJailDisk = "/rootfs.ext4";

    private bool UseJailer => Options.JailerPath is not null;

    private string JailBase => Inside(Path.Combine(Options.StateDirectory, "jail"));

    private string JailRoot(string name) => Inside(Path.Combine(JailBase, Path.GetFileName(Options.MonitorBinaryPath!), name, "root"));

    protected override IReadOnlyList<string> BuildLaunchArgv(VmConfig config)
    {
        if (!UseJailer)
        {
            return [Options.MonitorBinaryPath!, "--api-sock", config.Paths.ApiSocket];
        }

        return
        [
            Options.JailerPath!,
            "--id", config.Name,
            "--exec-file", Options.MonitorBinaryPath!,
            "--uid", Options.JailerUid.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "--gid", Options.JailerGid.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "--chroot-base-dir", JailBase,
            "--",
            "--api-sock", config.Paths.ApiSocket,
        ];
    }

    protected override IReadOnlyList<MicroVmApiRequest> BuildBootRequests(VmConfig config) =>
    [
        FirecrackerRequests.MachineConfig(config.Vcpu, config.MemoryMb),
        FirecrackerRequests.BootSource(config.Paths.ApiKernel),
        FirecrackerRequests.RootDrive(config.Paths.ApiDisk),
        FirecrackerRequests.NetworkInterface(config.Tap),
        FirecrackerRequests.Vsock(config.Cid, config.Paths.ApiVsock),
        FirecrackerRequests.InstanceStart(),
    ];

    protected override MicroVmApiRequest BuildGracefulStopRequest() => FirecrackerRequests.SendCtrlAltDel();

    protected override VmPaths PathsFor(string name)
    {
        if (!UseJailer)
        {
            return base.PathsFor(name);
        }

        var root = JailRoot(name);
        return new VmPaths(
            Inside(Path.Combine(root, "rootfs.ext4")),
            Inside(Path.Combine(root, "run", "firecracker.socket")),
            Inside(Path.Combine(root, "v.sock")),
            _inJailDisk,
            _inJailKernel,
            _inJailSocket,
            _inJailVsock);
    }

    protected override void PrepareDirectories(string name)
    {
        if (UseJailer)
        {
            Directory.CreateDirectory(Path.Combine(JailRoot(name), "run"));
        }
    }

    protected override void StageKernel(string name)
    {
        if (UseJailer)
        {
            File.Copy(Options.KernelPath!, Inside(Path.Combine(JailRoot(name), "vmlinux")), overwrite: true);
        }
    }

    protected override IEnumerable<string> RemovablePaths(string name) =>
        UseJailer ? [Inside(Path.Combine(JailBase, Path.GetFileName(Options.MonitorBinaryPath!), name))] : base.RemovablePaths(name);
}
