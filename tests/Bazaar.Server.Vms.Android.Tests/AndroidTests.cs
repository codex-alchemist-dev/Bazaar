using Bazaar.Contracts.Modules;
using Bazaar.Server.Vms.Devices;
using Bazaar.Server.Vms.Environments;
using Bazaar.Server.Vms.Lifecycle;

namespace Bazaar.Server.Vms.Android.Tests;

public sealed class FakeAndroidHost : IAndroidHost, IAndroidHostFacts, IAndroidWorkspace
{
    public List<string> Calls { get; } = [];
    public HashSet<int> Running { get; } = [];
    public HostFacts Facts { get; set; } = new("linux", "x64", new HashSet<string> { "android-emulator" });

    public HostFacts Current() => Facts;
    public string ResolveFile(Guid vmId, string relativePath) => "/ws/" + relativePath;
    public Task CreateAvdAsync(string avdName, string systemImagePackage, CancellationToken ct) { Calls.Add("create " + avdName); return Task.CompletedTask; }
    public Task DeleteAvdAsync(string avdName, CancellationToken ct) { Calls.Add("delete " + avdName); return Task.CompletedTask; }
    public Task StartEmulatorAsync(string avdName, int port, string? loadSnapshot, CancellationToken ct) { Running.Add(port); Calls.Add($"start {port} {loadSnapshot}"); return Task.CompletedTask; }
    public Task StopEmulatorAsync(int port, bool force, CancellationToken ct) { Running.Remove(port); Calls.Add($"stop {port} {force}"); return Task.CompletedTask; }
    public Task<bool> IsRunningAsync(int port, CancellationToken ct) => Task.FromResult(Running.Contains(port));
    public Task SaveSnapshotAsync(int port, string snapshotName, CancellationToken ct) { Calls.Add($"snap {port} {snapshotName}"); return Task.CompletedTask; }
    public Task InstallAsync(int port, string apkPath, CancellationToken ct) { Calls.Add($"install {port} {apkPath}"); return Task.CompletedTask; }
    public Task LaunchAsync(int port, string appId, CancellationToken ct) { Calls.Add($"launch {port} {appId}"); return Task.CompletedTask; }
    public Task<byte[]> ScreencapAsync(int port, CancellationToken ct) => Task.FromResult(new byte[] { 1, 2, 3 });
    public Task InputAsync(int port, DeviceInput input, CancellationToken ct) { Calls.Add($"input {port}"); return Task.CompletedTask; }
}

public sealed class AndroidBackendTests
{
    private static readonly Guid _image = Guid.CreateVersion7();
    private static readonly VmSpec _spec = new(VmOs.Android, 2, 4096, 16);

    private static (AndroidBackend Backend, FakeAndroidHost Host) Make(int max = 2)
    {
        var host = new FakeAndroidHost();
        var options = new AndroidOptions
        {
            SdkRoot = OperatingSystem.IsWindows() ? @"V:\TOOL\android-sdk" : "/opt/sdk",
            AvdHome = OperatingSystem.IsWindows() ? @"V:\TOOL\android-user" : "/opt/avd",
            SystemImagePackage = "system-images;android-35;google_apis;x86_64",
            SystemImageVersion = _image,
            MaxConcurrent = max,
        };
        return (new AndroidBackend(host, host, host, options), host);
    }

    [Fact]
    public async Task Create_rejects_other_os_and_unpinned_image()
    {
        var (b, _) = Make();
        await Assert.ThrowsAsync<NotSupportedException>(() => b.CreateChildAsync(new(_image), _spec with { Os = VmOs.Linux }, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<InvalidOperationException>(() => b.CreateChildAsync(new(Guid.NewGuid()), _spec, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Create_starts_nothing_until_start()
    {
        var (b, h) = Make();
        var vm = await b.CreateChildAsync(new(_image), _spec, TestContext.Current.CancellationToken);
        Assert.Empty(h.Running);
        Assert.Equal(VmState.Stopped, await b.GetStateAsync(vm, TestContext.Current.CancellationToken));
        await b.StartAsync(vm, TestContext.Current.CancellationToken);
        Assert.Single(h.Running);
        Assert.Equal(VmState.Running, await b.GetStateAsync(vm, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Concurrency_cap_is_enforced_and_ports_differ()
    {
        var (b, h) = Make(2);
        var vms = new List<VmHandle>();
        for (var i = 0; i < 3; i++) vms.Add(await b.CreateChildAsync(new(_image), _spec, TestContext.Current.CancellationToken));
        await b.StartAsync(vms[0], TestContext.Current.CancellationToken);
        await b.StartAsync(vms[1], TestContext.Current.CancellationToken);
        Assert.Equal(2, h.Running.Count);
        await Assert.ThrowsAsync<InvalidOperationException>(() => b.StartAsync(vms[2], TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Save_stops_emulator_and_records_Saved()
    {
        var (b, h) = Make();
        var vm = await b.CreateChildAsync(new(_image), _spec, TestContext.Current.CancellationToken);
        await b.StartAsync(vm, TestContext.Current.CancellationToken);
        await b.SaveStateAsync(vm, TestContext.Current.CancellationToken);
        Assert.Empty(h.Running);
        Assert.Equal(VmState.Saved, await b.GetStateAsync(vm, TestContext.Current.CancellationToken));
        Assert.Contains(h.Calls, c => c.StartsWith("snap ", StringComparison.Ordinal) && c.EndsWith("bz_saved", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Checkpoint_needs_stopped_vm_and_leaves_it_stopped()
    {
        var (b, h) = Make();
        var vm = await b.CreateChildAsync(new(_image), _spec, TestContext.Current.CancellationToken);
        await b.StartAsync(vm, TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<InvalidOperationException>(() => b.CheckpointAsync(vm, TestContext.Current.CancellationToken));
        await b.StopAsync(vm, StopMode.Graceful, TestContext.Current.CancellationToken);
        var cp = await b.CheckpointAsync(vm, TestContext.Current.CancellationToken);
        Assert.Equal(VmState.Stopped, await b.GetStateAsync(vm, TestContext.Current.CancellationToken));
        Assert.Empty(h.Running);
        await b.RestoreAsync(vm, cp, TestContext.Current.CancellationToken);
        Assert.Contains(h.Calls, c => c.Contains($"bz_cp_{cp.Value:N}"));
        Assert.Equal(VmState.Running, await b.GetStateAsync(vm, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task External_close_is_recorded_as_stopped()
    {
        var (b, h) = Make();
        var vm = await b.CreateChildAsync(new(_image), _spec, TestContext.Current.CancellationToken);
        await b.StartAsync(vm, TestContext.Current.CancellationToken);
        h.Running.Clear();
        Assert.Equal(VmState.Stopped, await b.GetStateAsync(vm, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Retire_stops_and_deletes()
    {
        var (b, h) = Make();
        var vm = await b.CreateChildAsync(new(_image), _spec, TestContext.Current.CancellationToken);
        await b.StartAsync(vm, TestContext.Current.CancellationToken);
        await b.RetireAsync(vm, TestContext.Current.CancellationToken);
        Assert.Empty(h.Running);
        Assert.Equal(VmState.Retired, await b.GetStateAsync(vm, TestContext.Current.CancellationToken));
        Assert.Contains(h.Calls, c => c.StartsWith("delete ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Device_calls_need_a_running_guest_and_validate_input()
    {
        var (b, h) = Make();
        var vm = await b.CreateChildAsync(new(_image), _spec, TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<InvalidOperationException>(() => b.LaunchAppAsync(vm.VmId, "com.example.app", TestContext.Current.CancellationToken));
        await b.StartAsync(vm, TestContext.Current.CancellationToken);
        await b.InstallAppAsync(vm.VmId, "out/app.apk", TestContext.Current.CancellationToken);
        await b.LaunchAppAsync(vm.VmId, "com.example.app", TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<ArgumentException>(() => b.InstallAppAsync(vm.VmId, "../etc/passwd", TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentException>(() => b.LaunchAppAsync(vm.VmId, "x; rm -rf /", TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentException>(() => b.SendInputAsync(vm.VmId, new DeviceInput.Text("a;b"), TestContext.Current.CancellationToken));
        Assert.Equal(3, (await b.CaptureAsync(vm.VmId, TestContext.Current.CancellationToken)).Png.Length);
        Assert.Contains("install 5554 /ws/out/app.apk", h.Calls);
    }

    [Fact]
    public void Probe_uses_host_requirements()
    {
        var (b, h) = Make();
        Assert.True(b.Probe().Usable);
        h.Facts = new("linux", "x64", new HashSet<string>());
        Assert.False(b.Probe().Usable);
        h.Facts = new("windows", "arm64", new HashSet<string> { "android-emulator" });
        Assert.False(b.Probe().Usable);
    }

    [Fact]
    public void Options_refuse_relative_or_empty_paths()
    {
        var o = new AndroidOptions { SdkRoot = "sdk", AvdHome = "/a", SystemImagePackage = "system-images;android-35;google_apis;x86_64", SystemImageVersion = _image };
        Assert.Throws<InvalidOperationException>(o.Validate);
        Assert.Throws<InvalidOperationException>(() => (o with { SdkRoot = "/s", SystemImagePackage = "a b" }).Validate());
    }

    [Fact]
    public void Capabilities_say_egress_is_not_enforced()
    {
        Assert.False(AndroidModule.Capabilities.EgressEnforced);
        Assert.True(AndroidModule.Capabilities.Headless);
        Assert.Equal(DeviceIsolation.HardwareVirtualised, AndroidModule.Capabilities.Isolation);
        Assert.Empty(HostCompatibility.ValidateRequirements(AndroidModule.Requirements));
    }
}

public sealed class AndroidArgvTests
{
    [Fact]
    public void Emulator_is_headless_with_loopback_port_and_no_shell()
    {
        var a = AndroidArgv.Emulator("bz_abc", 5554, "bz_saved");
        Assert.Contains("-no-window", a);
        Assert.Equal(["-snapshot", "bz_saved"], a.TakeLast(2));
        Assert.Throws<ArgumentException>(() => AndroidArgv.Emulator("a b", 5554));
        Assert.Throws<ArgumentOutOfRangeException>(() => AndroidArgv.Emulator("a", 5555));
        Assert.Throws<ArgumentOutOfRangeException>(() => AndroidArgv.Emulator("a", 80));
    }

    [Theory]
    [InlineData("hello world", "hello%sworld")]
    [InlineData("a@b.co", "a@b.co")]
    public void Text_encoding(string input, string expected) => Assert.Equal(expected, AndroidArgv.EncodeText(input));

    [Theory]
    [InlineData("a;b")]
    [InlineData("$(id)")]
    [InlineData("a\nb")]
    [InlineData("'x'")]
    [InlineData("a|b")]
    public void Text_refuses_shell_characters(string input) => Assert.ThrowsAny<ArgumentException>(() => AndroidArgv.EncodeText(input));

    [Fact]
    public void Adb_calls_always_name_the_emulator_serial()
    {
        Assert.Equal("emulator-5556", AndroidArgv.Install(5556, "/x.apk")[1]);
        Assert.Equal("emulator-5554", AndroidArgv.Input(5554, new DeviceInput.Tap(1, 2))[1]);
        Assert.Throws<ArgumentOutOfRangeException>(() => AndroidArgv.Input(5554, new DeviceInput.Tap(-1, 2)));
    }
}

public sealed class DeviceIdentifierTests
{
    [Theory]
    [InlineData("com.example.app", true)]
    [InlineData("app", false)]
    [InlineData("com..x", false)]
    [InlineData("com.example; ls", false)]
    public void App_ids(string id, bool ok)
    {
        if (ok) Assert.Equal(id, DeviceIdentifiers.AppId(id)); else Assert.Throws<ArgumentException>(() => DeviceIdentifiers.AppId(id));
    }

    [Theory]
    [InlineData("/abs")]
    [InlineData("a/../b")]
    [InlineData("a\\b")]
    [InlineData("")]
    [InlineData("a//b")]
    public void Workspace_paths_are_jailed(string p) => Assert.Throws<ArgumentException>(() => DeviceIdentifiers.WorkspaceRelativePath(p));
}
