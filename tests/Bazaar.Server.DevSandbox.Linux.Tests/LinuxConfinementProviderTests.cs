using Bazaar.Server.DevSandbox.Linux;

namespace Bazaar.Server.DevSandbox.Linux.Tests;

[System.Runtime.Versioning.SupportedOSPlatform("linux")]
public sealed class LinuxConfinementProviderTests
{
    private sealed class ScriptPlan(string script) : ILinuxSandboxLaunchPlan
    {
        public LinuxLaunchCommand Create(SandboxRunSpec spec, string workDirectory) => new("/bin/sh", ["-c", script]);
    }

    private sealed class FakeRunner(Func<LinuxProbeOutput> result) : ILinuxProbeRunner
    {
        public int Calls;

        public Task<LinuxProbeOutput> RunAsync(string file, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(result());
        }
    }

    private static SandboxRunSpec Spec(string runId = "run-1", int memoryMb = 256) =>
        new(runId, "mod", "1.0.0", SandboxEngineKind.Vm, [], [], 1UL, TimeoutSeconds: 30, MemoryLimitMb: memoryMb);

    private static LinuxConfinementProvider Real(string script) => new(new LinuxConfinementOptions(), new ScriptPlan(script));

    private static void RequireTested(LinuxConfinementProvider p) =>
        Assert.SkipUnless(p.Tested, "Unprivileged namespaces unavailable on this host: " + p.UntestedReason);

    [Fact]
    public void Os_is_linux()
    {
        Assert.Equal(SandboxOs.Linux, Real("true").Os);
    }

    [Fact]
    public void Self_test_on_this_host_is_consistent_with_its_reasons()
    {
        var p = Real("true");
        Assert.Equal(p.Tested, p.UntestedReason is null);
        Assert.Equal(p.SelfTestResult.Passed, p.Tested);
        Assert.Equal(p.SelfTestResult.Reasons.Count == 0, p.Tested);
    }

    [Fact]
    public async Task Confined_process_sees_only_loopback_pid_1_and_the_memory_limit_and_an_empty_environment()
    {
        var p = Real(
            "echo PID=$$; while IFS=: read -r name rest; do set -- $name; echo IF=$1; done < /proc/net/dev; " +
            "while read -r a b c d e; do if [ \"$a $b\" = \"Max address\" ]; then echo AS=$d; fi; done < /proc/self/limits; " +
            "echo CWD=$(pwd); echo VARS=$(env | wc -l)");
        RequireTested(p);

        await using var process = (LinuxSandboxProcess)await p.StartAsync(Spec(memoryMb: 128), TestContext.Current.CancellationToken);
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, process.ExitCode);
        var lines = process.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Contains("PID=1", lines);
        var ifs = lines.Where(l => l.StartsWith("IF=", StringComparison.Ordinal)).Where(l => l != "IF=Inter-|" && l != "IF=face").ToArray();
        Assert.Contains("IF=lo", ifs);
        Assert.DoesNotContain(ifs, l => l.StartsWith("IF=eth", StringComparison.Ordinal) || l.StartsWith("IF=en", StringComparison.Ordinal));
        Assert.Contains($"AS={128L * 1024 * 1024}", lines);
        Assert.Contains($"CWD={process.WorkDirectory}", lines);

        // sh adds PWD (and possibly OLDPWD/_) itself; nothing from the host may leak.
        Assert.DoesNotContain("HTTPS_PROXY", process.Stdout);
    }

    [Fact]
    public async Task Host_environment_is_not_inherited()
    {
        var p = Real("env");
        RequireTested(p);
        Environment.SetEnvironmentVariable("BAZAAR_SANDBOX_SECRET", "leak-me");
        try
        {
            await using var process = (LinuxSandboxProcess)await p.StartAsync(Spec(), TestContext.Current.CancellationToken);
            await process.WaitForExitAsync(TestContext.Current.CancellationToken);
            Assert.DoesNotContain("leak-me", process.Stdout);
            Assert.DoesNotContain("BAZAAR_SANDBOX_SECRET", process.Stdout);
            Assert.Contains("PATH=/usr/bin:/bin", process.Stdout);
        }
        finally
        {
            Environment.SetEnvironmentVariable("BAZAAR_SANDBOX_SECRET", null);
        }
    }

    [Fact]
    public async Task Work_directory_is_created_private_empty_and_removed_on_dispose()
    {
        var p = Real("ls -A");
        RequireTested(p);

        var process = (LinuxSandboxProcess)await p.StartAsync(Spec(), TestContext.Current.CancellationToken);
        var dir = process.WorkDirectory;
        Assert.True(Directory.Exists(dir));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(dir));
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);
        Assert.Equal(string.Empty, process.Stdout.Trim());

        await process.DisposeAsync();
        Assert.False(Directory.Exists(dir));
    }

    [Fact]
    public async Task Dispose_is_idempotent()
    {
        var p = Real("true");
        RequireTested(p);
        var process = await p.StartAsync(Spec(), TestContext.Current.CancellationToken);
        await process.DisposeAsync();
        await process.DisposeAsync();
        await process.KillAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Kill_stops_a_long_sleep_and_its_children()
    {
        var marker = "7777." + Environment.ProcessId;
        var p = Real($"sleep {marker} & sleep {marker}");
        RequireTested(p);

        await using var process = (LinuxSandboxProcess)await p.StartAsync(Spec(), TestContext.Current.CancellationToken);
        await Task.Delay(500, TestContext.Current.CancellationToken);
        Assert.False(process.HasExited);
        Assert.True(CountHostProcessesWithArgument(marker) >= 1);

        await process.KillAsync(TestContext.Current.CancellationToken);
        Assert.True(process.HasExited);

        for (var i = 0; i < 50 && CountHostProcessesWithArgument(marker) > 0; i++)
        {
            await Task.Delay(100, TestContext.Current.CancellationToken);
        }

        Assert.Equal(0, CountHostProcessesWithArgument(marker));
    }

    [Fact]
    public async Task Start_throws_when_not_tested_and_never_starts_a_process()
    {
        var runner = new FakeRunner(() => new LinuxProbeOutput(1, "", "unshare: unshare failed: Operation not permitted"));
        var p = new LinuxConfinementProvider(new LinuxConfinementOptions(), new ScriptPlan("true"), runner);

        Assert.False(p.Tested);
        Assert.Contains("(a)", p.UntestedReason);
        await Assert.ThrowsAsync<InvalidOperationException>(() => p.StartAsync(Spec(), TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Self_test_fails_when_the_network_is_not_isolated()
    {
        var runner = new FakeRunner(() => new LinuxProbeOutput(0, $"PID=1\nIF=lo\nIF=eth0\nAS={256L * 1024 * 1024},{256L * 1024 * 1024}\n", ""));
        var p = new LinuxConfinementProvider(new LinuxConfinementOptions(), new ScriptPlan("true"), runner);
        RequireToolsPresent();
        Assert.False(p.Tested);
        Assert.Contains("(b)", p.UntestedReason);
    }

    [Fact]
    public void Self_test_fails_when_pid_is_not_1()
    {
        var runner = new FakeRunner(() => new LinuxProbeOutput(0, $"PID=4242\nIF=lo\nAS={256L * 1024 * 1024},{256L * 1024 * 1024}\n", ""));
        var p = new LinuxConfinementProvider(new LinuxConfinementOptions(), new ScriptPlan("true"), runner);
        RequireToolsPresent();
        Assert.False(p.Tested);
        Assert.Contains("(c)", p.UntestedReason);
    }

    [Fact]
    public void Self_test_fails_when_the_limit_is_missing()
    {
        var runner = new FakeRunner(() => new LinuxProbeOutput(0, "PID=1\nIF=lo\nAS=unlimited,unlimited\n", ""));
        var p = new LinuxConfinementProvider(new LinuxConfinementOptions(), new ScriptPlan("true"), runner);
        RequireToolsPresent();
        Assert.False(p.Tested);
        Assert.Contains("(d)", p.UntestedReason);
    }

    [Fact]
    public void Self_test_passes_with_a_runner_that_reports_everything_correctly_and_is_cached()
    {
        var runner = new FakeRunner(() => new LinuxProbeOutput(0, $"PID=1\nIF=lo\nAS={256L * 1024 * 1024},{256L * 1024 * 1024}\n", ""));
        var p = new LinuxConfinementProvider(new LinuxConfinementOptions(), new ScriptPlan("true"), runner);
        RequireToolsPresent();
        Assert.True(p.Tested);
        Assert.Null(p.UntestedReason);
        _ = p.Tested;
        Assert.Equal(1, runner.Calls);
    }

    [Fact]
    public void Self_test_fails_when_the_runner_throws()
    {
        var runner = new FakeRunner(() => throw new InvalidOperationException("boom"));
        var p = new LinuxConfinementProvider(new LinuxConfinementOptions(), new ScriptPlan("true"), runner);
        Assert.False(p.Tested);
        Assert.Contains("boom", p.UntestedReason);
    }

    [Fact]
    public void Self_test_fails_when_tools_are_missing()
    {
        var runner = new FakeRunner(() => new LinuxProbeOutput(0, "PID=1\nIF=lo\n", ""));
        var options = new LinuxConfinementOptions { UnsharePath = "/nonexistent/unshare", PrlimitPath = "/nonexistent/prlimit" };
        var p = new LinuxConfinementProvider(options, new ScriptPlan("true"), runner);
        Assert.False(p.Tested);
        Assert.Contains("unshare", p.UntestedReason);
        Assert.Contains("prlimit", p.UntestedReason);
        Assert.Equal(0, runner.Calls);
    }

    [Fact]
    public async Task Start_rejects_a_bad_run_id()
    {
        var runner = new FakeRunner(() => new LinuxProbeOutput(0, $"PID=1\nIF=lo\nAS={256L * 1024 * 1024},{256L * 1024 * 1024}\n", ""));
        var p = new LinuxConfinementProvider(new LinuxConfinementOptions(), new ScriptPlan("true"), runner);
        RequireToolsPresent();
        await Assert.ThrowsAsync<ArgumentException>(() => p.StartAsync(Spec("../evil"), TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Command_line_is_an_argument_vector_with_all_namespaces_and_limits()
    {
        var args = LinuxCommandLine.BuildArguments(new LinuxConfinementOptions(), "/usr/bin/unshare", 64, 10, new LinuxLaunchCommand("/bin/x", ["a b", "; rm -rf /"]));
        Assert.Equal("--as=67108864", args[0]);
        Assert.Equal("--cpu=10", args[1]);
        foreach (var flag in new[] { "--user", "--map-root-user", "--net", "--pid", "--mount", "--ipc", "--uts", "--fork", "--kill-child", "--mount-proc" })
        {
            Assert.Contains(flag, args);
        }

        Assert.Equal(["/bin/x", "a b", "; rm -rf /"], args.TakeLast(3));
    }

    private static void RequireToolsPresent() =>
        Assert.SkipUnless(
            new LinuxConfinementOptions().UnsharePath is null && File.Exists("/usr/bin/unshare") && File.Exists("/usr/bin/prlimit") && File.Exists("/bin/sh"),
            "unshare, prlimit or sh not installed in the standard locations");

    private static int CountHostProcessesWithArgument(string marker)
    {
        var count = 0;
        foreach (var dir in Directory.EnumerateDirectories("/proc"))
        {
            if (!int.TryParse(Path.GetFileName(dir), out _))
            {
                continue;
            }

            try
            {
                if (File.ReadAllText(Path.Combine(dir, "cmdline")).Contains(marker, StringComparison.Ordinal)
                    && !File.ReadAllText(Path.Combine(dir, "cmdline")).Contains("dotnet", StringComparison.Ordinal))
                {
                    count++;
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        return count;
    }
}
