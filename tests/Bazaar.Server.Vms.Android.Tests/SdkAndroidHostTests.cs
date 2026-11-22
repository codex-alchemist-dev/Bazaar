using Bazaar.Server.Vms.Android.Sdk;
using Bazaar.Server.Vms.Devices;

namespace Bazaar.Server.Vms.Android.Tests;

public sealed class FakeRunner : IAndroidCommandRunner
{
    public List<AndroidCommand> Runs { get; } = [];
    public List<AndroidCommand> Detached { get; } = [];
    public FakeProcess? Process { get; set; }
    public Func<AndroidCommand, AndroidCommandResult> Respond { get; set; } = _ => new AndroidCommandResult(0, [], "");

    public Task<AndroidCommandResult> RunAsync(AndroidCommand command, CancellationToken ct)
    {
        Runs.Add(command);
        return Task.FromResult(Respond(command));
    }

    public IAndroidProcess StartDetached(AndroidCommand command)
    {
        Detached.Add(command);
        Process = new FakeProcess();
        return Process;
    }
}

public sealed class FakeProcess : IAndroidProcess
{
    private readonly TaskCompletionSource _exit = new();

    public bool HasExited => _exit.Task.IsCompleted;
    public bool Killed { get; private set; }
    public void Kill() { Killed = true; _exit.TrySetResult(); }
    public void ExitNormally() => _exit.TrySetResult();
    public Task WaitForExitAsync(CancellationToken ct) => _exit.Task.WaitAsync(ct);
    public void Dispose() { }
}

public sealed class SdkAndroidHostTests
{
    private static readonly string _sdk = OperatingSystem.IsWindows() ? @"V:\TOOL\android-sdk" : "/opt/sdk";
    private static readonly string _avd = OperatingSystem.IsWindows() ? @"V:\TOOL\android-user" : "/opt/avd";
    private static readonly string[] _expectedKeys = ["ANDROID_ADB_SERVER_PORT", "ANDROID_AVD_HOME", "ANDROID_HOME", "ANDROID_SDK_ROOT"];
    private static readonly byte[] _png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3];

    private static AndroidOptions Options() => new()
    {
        SdkRoot = _sdk,
        AvdHome = _avd,
        SystemImagePackage = "system-images;android-35;google_apis;x86_64",
        SystemImageVersion = Guid.NewGuid(),
    };

    private static (SdkAndroidHost Host, FakeRunner Runner) Make(TimeSpan? graceful = null)
    {
        var runner = new FakeRunner();
        var timeouts = new SdkAndroidTimeouts { GracefulStop = graceful ?? TimeSpan.FromSeconds(5) };
        return (new SdkAndroidHost(Options(), runner, timeouts), runner);
    }

    [Fact]
    public async Task Environment_holds_only_the_android_variables()
    {
        var (host, runner) = Make();
        await host.LaunchAsync(5554, "com.example.app", TestContext.Current.CancellationToken);
        await host.CreateAvdAsync("avd_one", Options().SystemImagePackage, TestContext.Current.CancellationToken);

        var adb = runner.Runs[0].Environment;
        Assert.Equal(_expectedKeys, adb.Keys.Order(StringComparer.Ordinal).ToArray());
        Assert.Equal(_sdk, adb["ANDROID_HOME"]);
        Assert.Equal(_sdk, adb["ANDROID_SDK_ROOT"]);
        Assert.Equal(_avd, adb["ANDROID_AVD_HOME"]);
        Assert.False(adb.ContainsKey("PATH"));

        var avdManager = runner.Runs[1].Environment;
        Assert.Equal(3, avdManager.Count);
        Assert.False(avdManager.ContainsKey("ANDROID_ADB_SERVER_PORT"));
    }

    [Fact]
    public async Task Arguments_are_exactly_the_argv_builders_output()
    {
        var (host, runner) = Make();
        var ct = TestContext.Current.CancellationToken;
        await host.StartEmulatorAsync("avd_one", 5556, "snap_a", ct);
        await host.InstallAsync(5556, "/ws/app.apk", ct);
        await host.LaunchAsync(5556, "com.example.app", ct);
        await host.InputAsync(5556, new DeviceInput.Tap(3, 4), ct);
        await host.CreateAvdAsync("avd_one", Options().SystemImagePackage, ct);

        Assert.Equal(AndroidArgv.Emulator("avd_one", 5556, "snap_a"), runner.Detached[0].Arguments);
        Assert.Equal(AndroidArgv.Install(5556, "/ws/app.apk"), runner.Runs[0].Arguments);
        Assert.Equal(AndroidArgv.Launch(5556, "com.example.app"), runner.Runs[1].Arguments);
        Assert.Equal(AndroidArgv.Input(5556, new DeviceInput.Tap(3, 4)), runner.Runs[2].Arguments);
        Assert.Equal(AndroidArgv.CreateAvd("avd_one", Options().SystemImagePackage, SdkAndroidHost.DefaultDevice), runner.Runs[3].Arguments);
    }

    [Fact]
    public async Task Executables_sit_under_the_sdk_root()
    {
        var (host, runner) = Make();
        var ct = TestContext.Current.CancellationToken;
        await host.StartEmulatorAsync("avd_one", 5554, null, ct);
        await host.LaunchAsync(5554, "com.example.app", ct);
        await host.DeleteAvdAsync("avd_one", ct);

        foreach (var exe in runner.Detached.Concat(runner.Runs).Select(c => c.Executable))
        {
            Assert.StartsWith(Path.GetFullPath(_sdk), exe, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Each_emulator_gets_its_own_adb_server_port()
    {
        var (host, runner) = Make();
        var ct = TestContext.Current.CancellationToken;
        await host.StartEmulatorAsync("avd_one", 5554, null, ct);
        await host.StartEmulatorAsync("avd_two", 5556, null, ct);

        var ports = runner.Detached.Select(c => c.Environment["ANDROID_ADB_SERVER_PORT"]).ToArray();
        Assert.NotEqual(ports[0], ports[1]);
        Assert.Equal(SdkAndroidHost.AdbServerPort(5554).ToString(System.Globalization.CultureInfo.InvariantCulture), ports[0]);
        Assert.Throws<ArgumentOutOfRangeException>(() => SdkAndroidHost.AdbServerPort(5555));
    }

    [Fact]
    public async Task Starting_twice_on_one_port_is_refused_until_it_exits()
    {
        var (host, runner) = Make();
        var ct = TestContext.Current.CancellationToken;
        await host.StartEmulatorAsync("avd_one", 5554, null, ct);
        Assert.True(await host.IsRunningAsync(5554, ct));
        await Assert.ThrowsAsync<InvalidOperationException>(() => host.StartEmulatorAsync("avd_one", 5554, null, ct));

        runner.Process!.ExitNormally();
        Assert.False(await host.IsRunningAsync(5554, ct));
        await host.StartEmulatorAsync("avd_one", 5554, null, ct);
    }

    [Fact]
    public async Task Graceful_stop_sends_emu_kill_and_does_not_kill_when_it_exits()
    {
        var (host, runner) = Make();
        var ct = TestContext.Current.CancellationToken;
        await host.StartEmulatorAsync("avd_one", 5554, null, ct);
        var process = runner.Process!;
        runner.Respond = c =>
        {
            if (c.Arguments.Contains("emu"))
            {
                process.ExitNormally();
            }

            return new AndroidCommandResult(0, [], "");
        };

        await host.StopEmulatorAsync(5554, force: false, ct);

        Assert.Equal(["-s", "emulator-5554", "emu", "kill"], runner.Runs[0].Arguments);
        Assert.False(process.Killed);
        Assert.False(await host.IsRunningAsync(5554, ct));
    }

    [Fact]
    public async Task Stop_kills_the_process_after_the_graceful_timeout()
    {
        var (host, runner) = Make(TimeSpan.FromMilliseconds(100));
        var ct = TestContext.Current.CancellationToken;
        await host.StartEmulatorAsync("avd_one", 5554, null, ct);

        await host.StopEmulatorAsync(5554, force: false, ct);

        Assert.True(runner.Process!.Killed);
    }

    [Fact]
    public async Task Forced_stop_kills_without_asking()
    {
        var (host, runner) = Make();
        var ct = TestContext.Current.CancellationToken;
        await host.StartEmulatorAsync("avd_one", 5554, null, ct);

        await host.StopEmulatorAsync(5554, force: true, ct);

        Assert.True(runner.Process!.Killed);
        Assert.DoesNotContain(runner.Runs, r => r.Arguments.Contains("emu"));
    }

    [Fact]
    public async Task Snapshot_save_uses_the_console_command_and_detects_refusal()
    {
        var (host, runner) = Make();
        var ct = TestContext.Current.CancellationToken;
        await host.SaveSnapshotAsync(5554, "checkpoint_one", ct);
        Assert.Equal(["-s", "emulator-5554", "emu", "avd", "snapshot", "save", "checkpoint_one"], runner.Runs[0].Arguments);

        runner.Respond = _ => new AndroidCommandResult(0, "KO: no such avd\r\n"u8.ToArray(), "");
        await Assert.ThrowsAsync<InvalidOperationException>(() => host.SaveSnapshotAsync(5554, "checkpoint_one", ct));
        await Assert.ThrowsAsync<ArgumentException>(() => host.SaveSnapshotAsync(5554, "bad name; rm", ct));
    }

    [Fact]
    public async Task Screencap_returns_png_and_rejects_junk()
    {
        var (host, runner) = Make();
        var ct = TestContext.Current.CancellationToken;
        runner.Respond = _ => new AndroidCommandResult(0, _png, "");
        Assert.Equal(_png, await host.ScreencapAsync(5554, ct));

        runner.Respond = _ => new AndroidCommandResult(0, "error: device offline"u8.ToArray(), "");
        await Assert.ThrowsAsync<InvalidOperationException>(() => host.ScreencapAsync(5554, ct));
        runner.Respond = _ => new AndroidCommandResult(0, [], "");
        await Assert.ThrowsAsync<InvalidOperationException>(() => host.ScreencapAsync(5554, ct));
    }

    [Fact]
    public async Task Nonzero_exit_throws_with_the_error_text()
    {
        var (host, runner) = Make();
        runner.Respond = _ => new AndroidCommandResult(1, [], "device offline");
        var e = await Assert.ThrowsAsync<InvalidOperationException>(() => host.LaunchAsync(5554, "com.example.app", TestContext.Current.CancellationToken));
        Assert.Contains("device offline", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Runner_timeout_propagates()
    {
        var (host, runner) = Make();
        runner.Respond = _ => throw new TimeoutException("slow");
        await Assert.ThrowsAsync<TimeoutException>(() => host.LaunchAsync(5554, "com.example.app", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Hostile_input_never_reaches_the_runner()
    {
        var (host, runner) = Make();
        var ct = TestContext.Current.CancellationToken;
        await Assert.ThrowsAsync<ArgumentException>(() => host.InstallAsync(5554, "--bad", ct));
        await Assert.ThrowsAsync<ArgumentException>(() => host.LaunchAsync(5554, "x; reboot", ct));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => host.LaunchAsync(5555, "com.example.app", ct));
        Assert.Empty(runner.Runs);
    }

    [Fact]
    public void Paths_refuse_escapes_and_relative_roots()
    {
        var paths = new AndroidSdkPaths(_sdk, windows: false);
        Assert.Throws<ArgumentException>(() => paths.Resolve("..", "etc"));
        Assert.Throws<ArgumentException>(() => paths.Resolve("a/../../b"));
        Assert.Throws<ArgumentException>(() => paths.Resolve("/etc/passwd"));
        Assert.Throws<ArgumentException>(() => new AndroidSdkPaths("relative/sdk", windows: false));
    }

    [Fact]
    public async Task Process_runner_kills_the_tree_on_timeout()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var runner = new ProcessAndroidCommandRunner();
        var command = new AndroidCommand("/bin/sleep", ["30"], new Dictionary<string, string>(), TimeSpan.FromMilliseconds(300));
        var started = DateTime.UtcNow;
        await Assert.ThrowsAsync<TimeoutException>(() => runner.RunAsync(command, TestContext.Current.CancellationToken));
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task Process_runner_caps_output_and_uses_only_the_given_environment()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var runner = new ProcessAndroidCommandRunner(maxStdoutBytes: 1024);
        var big = new AndroidCommand("/bin/sh", ["-c", "head -c 100000 /dev/zero"], new Dictionary<string, string>(), TimeSpan.FromSeconds(20));
        await Assert.ThrowsAsync<InvalidOperationException>(() => runner.RunAsync(big, TestContext.Current.CancellationToken));

        var env = new AndroidCommand("/usr/bin/env", [], new Dictionary<string, string> { ["ANDROID_HOME"] = "/x" }, TimeSpan.FromSeconds(20));
        var result = await new ProcessAndroidCommandRunner().RunAsync(env, TestContext.Current.CancellationToken);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal("ANDROID_HOME=/x", System.Text.Encoding.UTF8.GetString(result.Stdout).Trim());
    }
}

public sealed class SdkAndroidHostFactsTests
{
    private static AndroidOptions Options(string sdk) => new()
    {
        SdkRoot = sdk,
        AvdHome = "/opt/avd",
        SystemImagePackage = "system-images;android-35;google_apis;x86_64",
        SystemImageVersion = Guid.NewGuid(),
    };

    [Fact]
    public void Linux_needs_tools_and_kvm()
    {
        var all = new SdkAndroidHostFacts(Options("/opt/sdk"), _ => true, _ => true, osOverride: "linux").Current();
        Assert.Contains("android-emulator", all.Features);
        Assert.Equal("linux", all.OperatingSystem);

        var noKvm = new SdkAndroidHostFacts(Options("/opt/sdk"), _ => true, _ => false, osOverride: "linux").Current();
        Assert.Empty(noKvm.Features);

        var noTools = new SdkAndroidHostFacts(Options("/opt/sdk"), _ => false, _ => true, osOverride: "linux").Current();
        Assert.Empty(noTools.Features);

        var noAdb = new SdkAndroidHostFacts(Options("/opt/sdk"), p => !p.EndsWith("adb", StringComparison.Ordinal), _ => true, osOverride: "linux").Current();
        Assert.Empty(noAdb.Features);
    }

    [Fact]
    public void Windows_needs_the_hypervisor_platform_dll()
    {
        var sdk = OperatingSystem.IsWindows() ? @"V:\TOOL\android-sdk" : "/opt/sdk";
        var seen = new List<string>();
        bool Exists(string p)
        {
            seen.Add(p);
            return true;
        }

        var ok = new SdkAndroidHostFacts(Options(sdk), Exists, _ => false, () => Path.Combine(Path.GetTempPath(), "sys32"), "windows").Current();
        Assert.Contains("android-emulator", ok.Features);
        Assert.Contains(seen, p => p.EndsWith("WinHvPlatform.dll", StringComparison.Ordinal));

        var missing = new SdkAndroidHostFacts(Options(sdk), p => !p.EndsWith("WinHvPlatform.dll", StringComparison.Ordinal), _ => true, () => "sys32", "windows").Current();
        Assert.Empty(missing.Features);
    }

    [Fact]
    public void Unknown_os_fails_closed()
    {
        var facts = new SdkAndroidHostFacts(Options("/opt/sdk"), _ => true, _ => true, osOverride: "other").Current();
        Assert.Empty(facts.Features);
    }
}
