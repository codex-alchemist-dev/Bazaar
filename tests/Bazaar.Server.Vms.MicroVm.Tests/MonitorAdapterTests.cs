using Bazaar.Server.Vms.MicroVm.Monitors;

namespace Bazaar.Server.Vms.MicroVm.Tests;

internal sealed class FakeProcess : IMicroVmProcess
{
    private readonly TaskCompletionSource _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public bool HasExited => _exit.Task.IsCompleted;

    public int ExitCode => 0;

    public bool Killed { get; private set; }

    public void Exit() => _exit.TrySetResult();

    public Task WaitForExitAsync(CancellationToken ct) => _exit.Task.WaitAsync(ct);

    public void Kill()
    {
        Killed = true;
        _exit.TrySetResult();
    }

    public void Dispose()
    {
    }
}

internal sealed class FakeLauncher : IMicroVmProcessLauncher
{
    public List<IReadOnlyList<string>> Launched { get; } = [];

    public List<FakeProcess> Processes { get; } = [];

    public IMicroVmProcess Launch(IReadOnlyList<string> argv)
    {
        Launched.Add(argv);
        var p = new FakeProcess();
        Processes.Add(p);
        return p;
    }
}

internal sealed class FakeApi(FakeLauncher launcher) : IMicroVmApiClient
{
    public List<(string Socket, MicroVmApiRequest Request)> Sent { get; } = [];

    public bool ExitOnStopRequest { get; set; }

    public Task<MicroVmApiResponse> SendAsync(string socketPath, MicroVmApiRequest request, CancellationToken ct)
    {
        Sent.Add((socketPath, request));
        if (ExitOnStopRequest && (request.Path == "/actions" && request.Body!.Contains("SendCtrlAltDel", StringComparison.Ordinal) || request.Path.EndsWith("power-button", StringComparison.Ordinal)))
        {
            launcher.Processes[^1].Exit();
        }

        return Task.FromResult(new MicroVmApiResponse(204, string.Empty));
    }
}

internal sealed class FakeNet : IMicroVmTapNetwork
{
    public List<string> Events { get; } = [];

    public Task CreateAsync(string tapName, CancellationToken ct)
    {
        Events.Add("create " + tapName);
        return Task.CompletedTask;
    }

    public Task DeleteAsync(string tapName, CancellationToken ct)
    {
        Events.Add("delete " + tapName);
        return Task.CompletedTask;
    }
}

internal sealed class FakeCloner : IMicroVmDiskCloner
{
    public List<string> Reflinks { get; } = [];

    public bool ReflinkSupported(string directory) => true;

    public Task ReflinkAsync(string source, string destination, CancellationToken ct)
    {
        Reflinks.Add(destination);
        File.Copy(source, destination);
        return Task.CompletedTask;
    }
}

public sealed class MonitorRequestTests
{
    [Fact]
    public void Firecracker_bodies_are_exact()
    {
        Assert.Equal("""{"vcpu_count":2,"mem_size_mib":512}""", FirecrackerRequests.MachineConfig(2, 512).Body);
        Assert.Equal("/machine-config", FirecrackerRequests.MachineConfig(2, 512).Path);
        Assert.Equal("""{"kernel_image_path":"/k/vmlinux","boot_args":"console=ttyS0 reboot=k panic=1 pci=off"}""", FirecrackerRequests.BootSource("/k/vmlinux").Body);
        Assert.Equal("/boot-source", FirecrackerRequests.BootSource("/k").Path);
        Assert.Equal("""{"drive_id":"rootfs","path_on_host":"/d/a.ext4","is_root_device":true,"is_read_only":false}""", FirecrackerRequests.RootDrive("/d/a.ext4").Body);
        Assert.Equal("/drives/rootfs", FirecrackerRequests.RootDrive("/d").Path);
        Assert.Equal("""{"iface_id":"eth0","host_dev_name":"tap123"}""", FirecrackerRequests.NetworkInterface("tap123").Body);
        Assert.Equal("/network-interfaces/eth0", FirecrackerRequests.NetworkInterface("t").Path);
        Assert.Equal("""{"guest_cid":3,"uds_path":"/s/v"}""", FirecrackerRequests.Vsock(3, "/s/v").Body);
        Assert.Equal("/vsock", FirecrackerRequests.Vsock(3, "/s").Path);
        Assert.Equal("""{"action_type":"InstanceStart"}""", FirecrackerRequests.InstanceStart().Body);
        Assert.Equal("""{"action_type":"SendCtrlAltDel"}""", FirecrackerRequests.SendCtrlAltDel().Body);
        Assert.Equal("/actions", FirecrackerRequests.SendCtrlAltDel().Path);
        Assert.All(new[] { FirecrackerRequests.InstanceStart(), FirecrackerRequests.Vsock(3, "/s") }, r => Assert.Equal(HttpMethod.Put, r.Method));
    }

    [Fact]
    public void CloudHypervisor_bodies_are_exact()
    {
        var create = CloudHypervisorRequests.Create(2, 512, "/k", "/d.ext4", "tap1", 4, "/v.sock");
        Assert.Equal("/api/v1/vm.create", create.Path);
        Assert.Equal(
            """{"cpus":{"boot_vcpus":2,"max_vcpus":2},"memory":{"size":536870912},"payload":{"kernel":"/k","cmdline":"console=ttyS0 reboot=k panic=1 root=/dev/vda rw"},"disks":[{"path":"/d.ext4"}],"net":[{"tap":"tap1"}],"vsock":{"cid":4,"socket":"/v.sock"}}""",
            create.Body);
        Assert.Equal("/api/v1/vm.boot", CloudHypervisorRequests.Boot().Path);
        Assert.Equal("/api/v1/vm.shutdown", CloudHypervisorRequests.Shutdown().Path);
        Assert.Equal("/api/v1/vm.power-button", CloudHypervisorRequests.PowerButton().Path);
        Assert.Null(CloudHypervisorRequests.Boot().Body);
    }
}

public sealed class MonitorAdapterTests : IDisposable
{
    private const string _name = "mv0123456789abcdef0123456789abcdef";
    private const string _name2 = "mvfedcba9876543210fedcba9876543210";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "bazaar-mvm-" + Guid.NewGuid().ToString("N"));
    private readonly FakeLauncher _launcher = new();
    private readonly FakeNet _net = new();

    public MonitorAdapterTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "state"));
        File.WriteAllBytes(Path.Combine(_root, "vmlinux"), [1, 2, 3]);
        File.WriteAllBytes(Path.Combine(_root, "rootfs.ext4"), [4, 5, 6]);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string State => Path.Combine(_root, "state");

    private MicroVmOptions Options(bool jailer = false) => new()
    {
        ImageVersion = Guid.NewGuid(),
        StateDirectory = State,
        MonitorBinaryPath = Path.Combine(_root, "bin", "firecracker"),
        KernelPath = Path.Combine(_root, "vmlinux"),
        RootfsPath = Path.Combine(_root, "rootfs.ext4"),
        JailerPath = jailer ? Path.Combine(_root, "bin", "jailer") : null,
        JailerUid = jailer ? 1001 : 0,
        JailerGid = jailer ? 1001 : 0,
    };

    private (FirecrackerMonitor M, FakeApi Api) Firecracker(bool jailer = false, TimeSpan? stop = null, IMicroVmDiskCloner? cloner = null)
    {
        var api = new FakeApi(_launcher);
        return (new FirecrackerMonitor(Options(jailer), api, _launcher, _net, cloner, stop ?? TimeSpan.FromMilliseconds(50), TimeSpan.Zero), api);
    }

    [Fact]
    public async Task Create_makes_a_sparse_capped_copy_and_launches_nothing()
    {
        var (m, api) = Firecracker();
        await m.CreateDiskAsync(_name, 1, 256, 1, Ct);
        var disk = Path.Combine(State, _name + ".ext4");
        Assert.Equal(1024L * 1024 * 1024, new FileInfo(disk).Length);
        Assert.Empty(_launcher.Launched);
        Assert.Empty(api.Sent);
        Assert.Empty(_net.Events);
    }

    [Fact]
    public async Task Reflink_seam_is_used_when_supported()
    {
        var cloner = new FakeCloner();
        var (m, _) = Firecracker(cloner: cloner);
        await m.CreateDiskAsync(_name, 1, 256, 1, Ct);
        Assert.Single(cloner.Reflinks);
    }

    [Theory]
    [InlineData("../../etc/passwd")]
    [InlineData("mv../../../../x")]
    [InlineData("MV0123456789ABCDEF0123456789ABCDEF")]
    [InlineData("mv0123")]
    [InlineData("")]
    public async Task Bad_names_are_refused(string name)
    {
        var (m, _) = Firecracker();
        await Assert.ThrowsAsync<ArgumentException>(() => m.CreateDiskAsync(name, 1, 256, 1, Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => m.BootAsync(name, Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => m.DeleteAsync(name, Ct));
        Assert.False(MicroVmMonitorBase.IsValidName(name));
        Assert.Empty(Directory.GetFileSystemEntries(State));
    }

    [Fact]
    public async Task Disk_larger_than_cap_is_refused_and_cleaned_up()
    {
        var (m, _) = Firecracker();
        await using (var fs = new FileStream(Options().RootfsPath!, FileMode.Open))
        {
            fs.SetLength(2L * 1024 * 1024 * 1024);
        }

        await Assert.ThrowsAsync<InvalidOperationException>(() => m.CreateDiskAsync(_name, 1, 256, 1, Ct));
        Assert.Empty(Directory.GetFiles(State));
    }

    [Fact]
    public async Task Boot_launches_by_argv_and_configures_in_order()
    {
        var (m, api) = Firecracker();
        await m.CreateDiskAsync(_name, 2, 512, 1, Ct);
        await m.BootAsync(_name, Ct);
        var sock = Path.Combine(State, _name + ".sock");
        var tap = "tap" + _name[^12..];
        Assert.Equal([Options().MonitorBinaryPath!, "--api-sock", sock], _launcher.Launched.Single());
        Assert.Equal(["create " + tap], _net.Events);
        Assert.Equal(
            ["/machine-config", "/boot-source", "/drives/rootfs", "/network-interfaces/eth0", "/vsock", "/actions"],
            api.Sent.Select(s => s.Request.Path).ToArray());
        Assert.All(api.Sent, s => Assert.Equal(sock, s.Socket));
        Assert.Equal(
            $$"""{"guest_cid":3,"uds_path":{{System.Text.Json.JsonSerializer.Serialize(Path.Combine(State, _name + ".vsock"))}}}""",
            api.Sent[4].Request.Body);
        Assert.Equal("""{"action_type":"InstanceStart"}""", api.Sent[5].Request.Body);
        Assert.True(await m.IsRunningAsync(_name, Ct));
    }

    [Fact]
    public async Task Cids_are_unique_and_tap_names_fit()
    {
        var (m, api) = Firecracker();
        await m.CreateDiskAsync(_name, 1, 256, 1, Ct);
        await m.CreateDiskAsync(_name2, 1, 256, 1, Ct);
        await m.BootAsync(_name, Ct);
        await m.BootAsync(_name2, Ct);
        var cids = api.Sent.Where(s => s.Request.Path == "/vsock").Select(s => s.Request.Body!.Split(',')[0]).ToArray();
        Assert.Equal(2, cids.Distinct().Count());
        Assert.True(MicroVmMonitorBase.TapName(_name).Length <= 15);
        Assert.NotEqual(MicroVmMonitorBase.TapName(_name), MicroVmMonitorBase.TapName(_name2));
    }

    [Fact]
    public async Task Graceful_stop_sends_ctrl_alt_del_and_does_not_kill_when_guest_exits()
    {
        var (m, api) = Firecracker(stop: TimeSpan.FromSeconds(30));
        api.ExitOnStopRequest = true;
        await m.CreateDiskAsync(_name, 1, 256, 1, Ct);
        await m.BootAsync(_name, Ct);
        await m.StopAsync(_name, force: false, Ct);
        Assert.Equal("""{"action_type":"SendCtrlAltDel"}""", api.Sent[^1].Request.Body);
        Assert.False(_launcher.Processes[0].Killed);
        Assert.False(await m.IsRunningAsync(_name, Ct));
    }

    [Fact]
    public async Task Stop_escalates_to_kill_after_timeout()
    {
        var (m, _) = Firecracker(stop: TimeSpan.FromMilliseconds(50));
        await m.CreateDiskAsync(_name, 1, 256, 1, Ct);
        await m.BootAsync(_name, Ct);
        await m.StopAsync(_name, force: false, Ct);
        Assert.True(_launcher.Processes[0].Killed);
        Assert.Contains("delete tap" + _name[^12..], _net.Events);
    }

    [Fact]
    public async Task Force_stop_kills_without_calling_the_api()
    {
        var (m, api) = Firecracker();
        await m.CreateDiskAsync(_name, 1, 256, 1, Ct);
        await m.BootAsync(_name, Ct);
        var before = api.Sent.Count;
        await m.StopAsync(_name, force: true, Ct);
        Assert.Equal(before, api.Sent.Count);
        Assert.True(_launcher.Processes[0].Killed);
    }

    [Fact]
    public async Task Delete_removes_disk_socket_vsock_and_tap()
    {
        var (m, _) = Firecracker();
        await m.CreateDiskAsync(_name, 1, 256, 1, Ct);
        await m.BootAsync(_name, Ct);
        File.WriteAllText(Path.Combine(State, _name + ".sock"), string.Empty);
        File.WriteAllText(Path.Combine(State, _name + ".vsock"), string.Empty);
        await m.DeleteAsync(_name, Ct);
        Assert.Empty(Directory.GetFileSystemEntries(State));
        Assert.True(_launcher.Processes[0].Killed);
        Assert.Equal("delete tap" + _name[^12..], _net.Events[^1]);
        await Assert.ThrowsAsync<KeyNotFoundException>(() => m.BootAsync(_name, Ct));
    }

    [Fact]
    public async Task Jailer_wraps_launch_and_keeps_files_inside_state_directory()
    {
        var (m, api) = Firecracker(jailer: true);
        await m.CreateDiskAsync(_name, 1, 256, 1, Ct);
        await m.BootAsync(_name, Ct);
        var opts = Options(jailer: true);
        Assert.Equal(
            [
                opts.JailerPath!, "--id", _name, "--exec-file", opts.MonitorBinaryPath!, "--uid", "1001", "--gid", "1001",
                "--chroot-base-dir", Path.Combine(State, "jail"), "--", "--api-sock", "/run/firecracker.socket",
            ],
            _launcher.Launched.Single());
        Assert.StartsWith(Path.Combine(State, "jail", "firecracker", _name, "root"), api.Sent[0].Socket, StringComparison.Ordinal);
        Assert.Contains("\"kernel_image_path\":\"/vmlinux\"", api.Sent[1].Request.Body, StringComparison.Ordinal);
        await m.DeleteAsync(_name, Ct);
        Assert.False(Directory.Exists(Path.Combine(State, "jail", "firecracker", _name)));
    }

    [Fact]
    public async Task CloudHypervisor_uses_its_own_launch_and_requests()
    {
        var api = new FakeApi(_launcher);
        var m = new CloudHypervisorMonitor(Options(), api, _launcher, _net, null, TimeSpan.FromMilliseconds(50), TimeSpan.Zero);
        await m.CreateDiskAsync(_name, 1, 256, 1, Ct);
        await m.BootAsync(_name, Ct);
        Assert.Equal([Options().MonitorBinaryPath!, "--api-socket", Path.Combine(State, _name + ".sock")], _launcher.Launched.Single());
        Assert.Equal(["/api/v1/vm.create", "/api/v1/vm.boot"], api.Sent.Select(s => s.Request.Path).ToArray());
        await m.StopAsync(_name, force: false, Ct);
        Assert.Equal("/api/v1/vm.power-button", api.Sent[^1].Request.Path);
        Assert.True(_launcher.Processes[0].Killed);
    }

    [Fact]
    public void Options_require_absolute_paths_and_jailer_ids()
    {
        Assert.Throws<InvalidOperationException>(() => (Options() with { KernelPath = "vmlinux" }).Validate());
        Assert.Throws<InvalidOperationException>(() => (Options() with { JailerPath = Path.Combine(_root, "jailer") }).Validate());
        Options(jailer: true).Validate();
        Assert.Throws<InvalidOperationException>(() => new FirecrackerMonitor(Options() with { KernelPath = null }, new FakeApi(_launcher), _launcher, _net));
    }
}
