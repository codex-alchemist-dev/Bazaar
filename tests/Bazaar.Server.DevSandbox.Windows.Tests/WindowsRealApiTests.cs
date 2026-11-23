using System.Net;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using Bazaar.Server.DevSandbox.Windows;

namespace Bazaar.Server.DevSandbox.Windows.Tests;

/// <summary>
/// Tests that exercise the real Win32 implementation (job object, AppContainer profile, ACLs, CreateProcess). They need a Windows
/// host with AppContainer and curl.exe; on any other host each one is skipped (not passed). They have not been run on Windows by the
/// author: the first run on the owner's PC is the first real verification. Run them with
/// <c>dotnet test tests/Bazaar.Server.DevSandbox.Windows.Tests</c> on Windows.
/// </summary>
[SupportedOSPlatform("windows")]
[Trait("Platform", "Windows")]
public sealed partial class WindowsRealApiTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "bazaar-win-real-" + Guid.NewGuid().ToString("N"));
    private readonly string _system = Environment.SystemDirectory;

    public WindowsRealApiTests()
    {
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private sealed class Plan(Func<WindowsLaunchCommand> create) : IWindowsSandboxLaunchPlan
    {
        public WindowsLaunchCommand Create(SandboxRunSpec spec, string workDirectory) => create();
    }

    private static void RequireWindows() => Assert.SkipUnless(OperatingSystem.IsWindows(), "Real Win32 tests run on Windows only.");

    private WindowsConfinementOptions Options() => new() { WorkRoot = _root };

    private static WindowsJobSpec JobSpec(int memoryMb = 256) =>
        new(memoryMb * 1024L * 1024, memoryMb * 1024L * 1024, TimeSpan.FromSeconds(120), 16);

    private static SandboxRunSpec Spec(int memoryMb = 128) =>
        new("real-1", "mod", "1.0.0", SandboxEngineKind.Vm, [], [], 1UL, TimeoutSeconds: 60, MemoryLimitMb: memoryMb);

    private static WindowsLaunchCommand Spin(string work)
    {
        File.WriteAllText(Path.Combine(work, "spin.cmd"), "@cmd /d /c spin2.cmd\r\n");
        File.WriteAllText(Path.Combine(work, "spin2.cmd"), "@for /l %%i in (1,1,2000000000) do @rem\r\n");
        return new WindowsLaunchCommand(Path.Combine(Environment.SystemDirectory, "cmd.exe"), ["/d", "/c", "spin.cmd"]);
    }

    private WindowsSandboxProcess Launch(string label, Func<string, WindowsLaunchCommand> command, WindowsConfinementMode mode = WindowsConfinementMode.AppContainer, int memoryMb = 256) =>
        WindowsLauncher.Launch(new Win32ConfinementApi(), Options(), label, JobSpec(memoryMb), mode, command, allowShell: true);

    [Fact]
    public async Task The_self_test_passes_on_this_host()
    {
        RequireWindows();
        var result = await WindowsConfinementSelfTest.RunAsync(Options(), TestContext.Current.CancellationToken);
        Assert.True(result.Passed, result.Summary);
        Assert.Equal(WindowsConfinementSelfTest.AppContainerLevel, result.HardeningLevel);
    }

    [Fact]
    public async Task The_provider_runs_curl_version_confined_and_cleans_up()
    {
        RequireWindows();
        var plan = new Plan(() => new WindowsLaunchCommand(Path.Combine(_system, "curl.exe"), ["--version"]));
        var provider = new WindowsConfinementProvider(Options(), plan);
        Assert.SkipUnless(provider.Tested, "Provider untested on this host: " + provider.UntestedReason);

        var process = (WindowsSandboxProcess)await provider.StartAsync(Spec(), TestContext.Current.CancellationToken);
        var dir = process.WorkDirectory;
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);
        Assert.Equal(0, process.ExitCode);
        Assert.Contains("curl", process.Stdout, StringComparison.OrdinalIgnoreCase);

        await process.DisposeAsync();
        Assert.False(Directory.Exists(dir));
    }

    [Fact]
    public async Task Kill_terminates_the_job_and_every_process_in_it()
    {
        RequireWindows();
        await using var process = Launch("real-kill", Spin);

        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (process.Job.ProcessIds().Count < 2 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(50, TestContext.Current.CancellationToken);
        }

        Assert.True(process.Job.ProcessIds().Count >= 2, "the child cmd.exe should be in the job");
        Assert.False(process.HasExited);

        await process.KillAsync(TestContext.Current.CancellationToken);

        Assert.True(process.HasExited);
        Assert.Equal(1, process.ExitCode);
        await Task.Delay(200, TestContext.Current.CancellationToken);
        Assert.Empty(process.Job.ProcessIds());
    }

    [Fact]
    public async Task Closing_the_job_handle_kills_the_process()
    {
        RequireWindows();
        await using var process = Launch("real-close", Spin);
        Assert.False(process.HasExited);

        process.Job.Dispose();

        Assert.True(await process.Process.WaitForExitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task The_appcontainer_cannot_read_or_write_outside_its_work_directory()
    {
        RequireWindows();
        var outside = Path.Combine(_root, "outside");
        Directory.CreateDirectory(outside);
        var secret = Path.Combine(outside, "secret.txt");
        File.WriteAllText(secret, "TOP-SECRET-VALUE");
        var written = Path.Combine(outside, "written.txt");

        await using var process = Launch("real-files", work =>
        {
            File.WriteAllText(
                Path.Combine(work, "files.cmd"),
                $"@echo off\r\necho PROBE=started\r\ntype \"{secret}\"\r\necho leak> \"{written}\"\r\necho ok> probe.txt\r\n");
            return new WindowsLaunchCommand(Path.Combine(_system, "cmd.exe"), ["/d", "/c", "files.cmd"]);
        });
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);

        Assert.Contains("PROBE=started", process.Stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("TOP-SECRET-VALUE", process.Stdout, StringComparison.Ordinal);
        Assert.False(File.Exists(written));
        Assert.True(File.Exists(Path.Combine(process.WorkDirectory, "probe.txt")), "the work directory itself must be writable");
    }

    [Fact]
    public async Task The_appcontainer_has_no_network()
    {
        RequireWindows();
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        await using var process = Launch("real-net", _ =>
            new WindowsLaunchCommand(Path.Combine(_system, "curl.exe"), ["--silent", "--connect-timeout", "5", "--max-time", "8", "-o", "NUL", $"http://127.0.0.1:{port}/"]));
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);

        Assert.False(listener.Pending(), "the confined process must not reach the listener");
        Assert.True(process.ExitCode is 7 or 28, "curl exit code " + process.ExitCode);
    }

    [Fact]
    public async Task The_job_holds_the_requested_limits()
    {
        RequireWindows();
        await using var process = Launch("real-limits", Spin, memoryMb: 64);

        var limits = process.Job.QueryLimits();

        Assert.True(limits.KillOnJobClose);
        Assert.False(limits.BreakawayAllowed);
        Assert.Equal(64L * 1024 * 1024, limits.ProcessMemoryBytes);
        Assert.Equal(64L * 1024 * 1024, limits.JobMemoryBytes);
        Assert.Equal(16, limits.ActiveProcessLimit);
        Assert.Equal(TimeSpan.FromSeconds(120), limits.CpuTime);
        Assert.Equal(0xFFu, limits.UiRestrictions);
    }

    [Fact]
    public async Task The_environment_is_scrubbed()
    {
        RequireWindows();
        await using var process = Launch("real-env", _ => new WindowsLaunchCommand(Path.Combine(_system, "cmd.exe"), ["/d", "/c", "set"]));
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);

        Assert.Contains("SystemRoot=", process.Stdout, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotMatch(PathLine(), process.Stdout);
        Assert.DoesNotContain("USERPROFILE=", process.Stdout, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("USERNAME=", process.Stdout, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task The_restricted_token_fallback_starts_a_process_inside_the_job()
    {
        RequireWindows();
        await using var process = Launch(
            "real-restricted",
            _ => new WindowsLaunchCommand(Path.Combine(_system, "curl.exe"), ["--version"]),
            WindowsConfinementMode.RestrictedToken);
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, process.ExitCode);
        Assert.Contains("curl", process.Stdout, StringComparison.OrdinalIgnoreCase);
    }

    [GeneratedRegex(@"^PATH=", RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex PathLine();
}
