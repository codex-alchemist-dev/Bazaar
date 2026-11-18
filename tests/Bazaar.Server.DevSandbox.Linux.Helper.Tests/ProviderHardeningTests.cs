using Bazaar.Server.DevSandbox.Linux;

namespace Bazaar.Server.DevSandbox.Linux.Helper.Tests;

/// <summary>The Linux provider with the hardening helper configured: command line, policy hand-off, self-test verdicts and real launches.</summary>
[System.Runtime.Versioning.SupportedOSPlatform("linux")]
public sealed class ProviderHardeningTests
{
    private const long _probeBytes = 256L * 1024 * 1024;

    private sealed class ScriptPlan(string script) : ILinuxSandboxLaunchPlan
    {
        public LinuxLaunchCommand Create(SandboxRunSpec spec, string workDirectory) => new("/bin/sh", ["-c", script]);
    }

    private sealed class FakeRunner(Func<IReadOnlyList<string>, LinuxProbeOutput> respond) : ILinuxProbeRunner
    {
        public int Calls;

        public Task<LinuxProbeOutput> RunAsync(string file, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(respond(arguments));
        }
    }

    private static SandboxRunSpec Spec(string runId = "run-1", int memoryMb = 256) =>
        new(runId, "mod", "1.0.0", SandboxEngineKind.Vm, [], [], 1UL, TimeoutSeconds: 30, MemoryLimitMb: memoryMb);

    private static LinuxConfinementOptions HardenedOptions() => new() { HelperPath = HelperHost.HelperPath, HelperEnvironment = HelperHost.StartupEnvironment };

    private static LinuxConfinementProvider RealHardened(string script) => new(HardenedOptions(), new ScriptPlan(script));

    private static void RequireHardened(LinuxConfinementProvider p)
    {
        Assert.SkipUnless(File.Exists(HelperHost.HelperPath), "helper apphost not built next to the tests");
        Assert.SkipUnless(p.Tested, "hardened self-test does not pass on this host (no Landlock or no unprivileged namespaces?): " + p.UntestedReason);
    }

    private static void RequireToolsPresent() =>
        Assert.SkipUnless(File.Exists("/usr/bin/unshare") && File.Exists("/usr/bin/prlimit") && File.Exists("/bin/sh"), "unshare, prlimit or sh not installed in the standard locations");

    private static string AllGoodStdout() =>
        $"PID=1\nIF=lo\nAS={_probeBytes},{_probeBytes}\nNNP=1\nSECCOMP=2\nOUTSIDE_READ=denied\nOUTSIDE_WRITE=denied\nWORKDIR=ok\nNESTED=ok\n";

    private static LinuxProbeOutput Respond(IReadOnlyList<string> args, string stdout, LinuxProbeOutput? socket = null) =>
        args.Contains("--self-test") ? socket ?? new LinuxProbeOutput(159, "", "bazaar-sandbox-helper: ready\n") : new LinuxProbeOutput(0, stdout, "");

    private static LinuxConfinementProvider WithFake(Func<IReadOnlyList<string>, LinuxProbeOutput> respond, out FakeRunner runner)
    {
        runner = new FakeRunner(respond);
        var options = new LinuxConfinementOptions { HelperPath = "/bin/sh" };
        return new LinuxConfinementProvider(options, new ScriptPlan("true"), runner);
    }

    // ---- command line and policy -------------------------------------------------------------------------------------------

    [Fact]
    public void Hardened_command_line_runs_the_helper_inside_unshare_and_moves_the_address_space_limit_into_the_policy()
    {
        var launch = new LinuxHardeningLaunch("/opt/helper", "/work/.bazaar-policy.json");
        var args = LinuxCommandLine.BuildArguments(new LinuxConfinementOptions(), "/usr/bin/unshare", 64, 10, new LinuxLaunchCommand("/bin/x", ["a b", "; rm -rf /"]), launch);

        Assert.DoesNotContain(args, a => a.StartsWith("--as=", StringComparison.Ordinal));
        Assert.Equal("--cpu=10", args[0]);
        var firstSeparator = args.ToList().IndexOf("--");
        Assert.Equal("/usr/bin/unshare", args[firstSeparator + 1]);
        var helperIndex = args.ToList().IndexOf("/opt/helper");
        Assert.True(helperIndex > firstSeparator);
        Assert.Equal(["/opt/helper", "--policy", "/work/.bazaar-policy.json", "--delete-policy-after-read", "--", "/bin/x", "a b", "; rm -rf /"], args.Skip(helperIndex));
        foreach (var flag in LinuxCommandLine.UnshareFlags)
        {
            Assert.Contains(flag, args);
        }
    }

    [Fact]
    public void Without_a_helper_the_command_line_is_unchanged()
    {
        var args = LinuxCommandLine.BuildArguments(new LinuxConfinementOptions(), "/usr/bin/unshare", 64, 10, new LinuxLaunchCommand("/bin/x", []));

        Assert.Equal("--as=67108864", args[0]);
        Assert.DoesNotContain("--policy", args);
    }

    [Fact]
    public void The_policy_the_provider_writes_is_accepted_by_the_helpers_parser_and_is_least_privilege()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "The policy paths are POSIX paths; Path handling differs on Windows.");
        var options = new LinuxConfinementOptions { Path = "/usr/bin:/bin" };
        var command = new LinuxLaunchCommand("/opt/mod/bin/run", ["x"], ["/opt/mod/data"]);

        var json = LinuxHardeningPolicy.Serialize(LinuxHardeningPolicy.Create(options, command, "/tmp/work-1", 134217728));
        var parsed = HelperPolicyParser.Parse(json, out var error);

        Assert.Null(error);
        Assert.NotNull(parsed);
        Assert.Equal(["/tmp/work-1"], parsed.WritePaths);
        Assert.Contains("/opt/mod/bin", parsed.ReadPaths);
        Assert.Contains("/opt/mod/data", parsed.ReadPaths);
        Assert.Contains("/lib", parsed.ReadPaths);
        Assert.Contains("/usr/lib", parsed.ReadPaths);
        Assert.DoesNotContain("/", parsed.ReadPaths);
        Assert.DoesNotContain("/tmp", parsed.ReadPaths);
        Assert.DoesNotContain("/etc", parsed.ReadPaths);
        Assert.Equal(134217728L, parsed.AddressSpaceBytes);
        Assert.Equal("/usr/bin:/bin", parsed.Path);
        Assert.Empty(parsed.ExtraSyscalls);
    }

    [Fact]
    public void Hardening_is_off_by_default()
    {
        Assert.Null(new LinuxConfinementOptions().HelperPath);
        Assert.Null(new LinuxConfinementOptions().HelperEnvironment);
    }

    // ---- self-test verdicts with a scripted runner --------------------------------------------------------------------------

    [Fact]
    public void Without_a_helper_the_proved_level_is_namespaces_and_rlimits()
    {
        RequireToolsPresent();
        var runner = new FakeRunner(_ => new LinuxProbeOutput(0, $"PID=1\nIF=lo\nAS={_probeBytes},{_probeBytes}\n", ""));
        var p = new LinuxConfinementProvider(new LinuxConfinementOptions(), new ScriptPlan("true"), runner);

        Assert.True(p.Tested);
        Assert.Equal("namespaces+rlimits", p.HardeningLevel);
    }

    [Fact]
    public void A_scripted_runner_that_proves_everything_gives_the_hardened_level()
    {
        RequireToolsPresent();
        var p = WithFake(a => Respond(a, AllGoodStdout()), out var runner);

        Assert.True(p.Tested);
        Assert.Null(p.UntestedReason);
        Assert.Equal("namespaces+rlimits+landlock+seccomp", p.HardeningLevel);
        Assert.Equal(2, runner.Calls);
    }

    [Theory]
    [InlineData("NNP=1\n", "NNP=0\n", "(f)")]
    [InlineData("SECCOMP=2\n", "SECCOMP=0\n", "(g)")]
    [InlineData("OUTSIDE_READ=denied\n", "OUTSIDE_READ=other:bazaar-canary\n", "(h)")]
    [InlineData("OUTSIDE_WRITE=denied\n", "OUTSIDE_WRITE=other:\n", "(i)")]
    [InlineData("WORKDIR=ok\n", "WORKDIR=failed\n", "(j)")]
    [InlineData("NESTED=ok\n", "", "(k)")]
    [InlineData("AS=268435456,268435456\n", "AS=unlimited,unlimited\n", "(d)")]
    public void Any_failed_hardening_check_fails_the_self_test_and_the_level_stays_at_the_base(string good, string bad, string label)
    {
        RequireToolsPresent();
        var stdout = AllGoodStdout().Replace(good, bad, StringComparison.Ordinal);
        var p = WithFake(a => Respond(a, stdout), out _);

        Assert.False(p.Tested);
        Assert.Contains(label, p.UntestedReason, StringComparison.Ordinal);
        Assert.Equal("namespaces+rlimits", p.HardeningLevel);
    }

    [Fact]
    public void The_canary_text_in_the_output_fails_the_read_check_even_if_the_probe_claims_denial()
    {
        RequireToolsPresent();
        var stdout = AllGoodStdout() + "bazaar-canary-must-stay-unreadable\n";
        var p = WithFake(a => Respond(a, stdout), out _);

        Assert.False(p.Tested);
        Assert.Contains("(h)", p.UntestedReason, StringComparison.Ordinal);
    }

    [Fact]
    public void A_socket_probe_that_survives_fails_the_self_test()
    {
        RequireToolsPresent();
        var p = WithFake(a => Respond(a, AllGoodStdout(), new LinuxProbeOutput(3, "", "bazaar-sandbox-helper: ready\nbazaar-sandbox-helper: SOCKET_ALLOWED\n")), out _);

        Assert.False(p.Tested);
        Assert.Contains("(l)", p.UntestedReason, StringComparison.Ordinal);
    }

    [Fact]
    public void A_socket_probe_that_dies_some_other_way_does_not_count_as_a_seccomp_denial()
    {
        RequireToolsPresent();
        var p = WithFake(a => Respond(a, AllGoodStdout(), new LinuxProbeOutput(1, "", "something else")), out _);

        Assert.False(p.Tested);
        Assert.Contains("(l)", p.UntestedReason, StringComparison.Ordinal);
    }

    [Fact]
    public void A_helper_that_refuses_for_lack_of_landlock_makes_the_provider_untested_with_no_downgrade()
    {
        RequireToolsPresent();
        var p = WithFake(_ => new LinuxProbeOutput(70, "", "bazaar-sandbox-helper: Landlock is not available (errno 38); refusing to run without file system confinement"), out var runner);

        Assert.False(p.Tested);
        Assert.Contains("exit 70", p.UntestedReason, StringComparison.Ordinal);
        Assert.Contains("Landlock is not available", p.UntestedReason, StringComparison.Ordinal);
        Assert.Equal("namespaces+rlimits", p.HardeningLevel);
        Assert.Equal(1, runner.Calls);
    }

    [Fact]
    public async Task An_untested_hardened_provider_never_starts_a_process()
    {
        RequireToolsPresent();
        var p = WithFake(_ => new LinuxProbeOutput(70, "", "no landlock"), out _);

        await Assert.ThrowsAsync<InvalidOperationException>(() => p.StartAsync(Spec(), TestContext.Current.CancellationToken));
    }

    [Fact]
    public void A_missing_helper_file_fails_the_self_test_without_running_anything()
    {
        RequireToolsPresent();
        var runner = new FakeRunner(_ => new LinuxProbeOutput(0, "", ""));
        var options = new LinuxConfinementOptions { HelperPath = "/nonexistent/bazaar-helper" };
        var p = new LinuxConfinementProvider(options, new ScriptPlan("true"), runner);

        Assert.False(p.Tested);
        Assert.Contains("(e)", p.UntestedReason, StringComparison.Ordinal);
        Assert.Equal(0, runner.Calls);
    }

    [Fact]
    public void The_self_test_leaves_no_directories_behind()
    {
        RequireToolsPresent();
        var root = Directory.CreateTempSubdirectory("bazaar-selftest-root-").FullName;
        try
        {
            var runner = new FakeRunner(a => Respond(a, AllGoodStdout()));
            var options = new LinuxConfinementOptions { HelperPath = "/bin/sh", WorkRoot = root };
            var p = new LinuxConfinementProvider(options, new ScriptPlan("true"), runner);

            Assert.True(p.Tested);
            Assert.Empty(Directory.GetFileSystemEntries(root));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    // ---- real launches through the helper ---------------------------------------------------------------------------------------

    [Fact]
    public void The_real_self_test_proves_the_hardened_level_on_this_host()
    {
        var p = RealHardened("true");
        RequireHardened(p);

        Assert.True(p.SelfTestResult.Passed);
        Assert.Equal("namespaces+rlimits+landlock+seccomp", p.HardeningLevel);
    }

    [Fact]
    public async Task A_real_hardened_run_has_no_new_privs_a_filter_the_memory_limit_and_only_path_in_its_environment()
    {
        var p = RealHardened(
            "while read -r k v; do case $k in NoNewPrivs:|Seccomp:) echo \"$k $v\";; esac; done < /proc/self/status; " +
            "while read -r a b c d e; do if [ \"$a $b\" = \"Max address\" ]; then echo AS=$d; fi; done < /proc/self/limits; env");
        RequireHardened(p);
        Environment.SetEnvironmentVariable("BAZAAR_SANDBOX_SECRET", "leak-me");
        try
        {
            await using var process = (LinuxSandboxProcess)await p.StartAsync(Spec(memoryMb: 128), TestContext.Current.CancellationToken);
            await process.WaitForExitAsync(TestContext.Current.CancellationToken);

            Assert.Equal(0, process.ExitCode);
            Assert.Contains("NoNewPrivs: 1", process.Stdout, StringComparison.Ordinal);
            Assert.Contains("Seccomp: 2", process.Stdout, StringComparison.Ordinal);
            Assert.Contains($"AS={128L * 1024 * 1024}", process.Stdout, StringComparison.Ordinal);
            Assert.Contains("PATH=/usr/bin:/bin", process.Stdout, StringComparison.Ordinal);
            Assert.DoesNotContain("leak-me", process.Stdout, StringComparison.Ordinal);
            Assert.DoesNotContain("DOTNET_", process.Stdout, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable("BAZAAR_SANDBOX_SECRET", null);
        }
    }

    [Fact]
    public async Task A_real_hardened_run_cannot_read_or_write_outside_its_work_directory()
    {
        var outside = Directory.CreateTempSubdirectory("bazaar-provider-outside-").FullName;
        var canary = Path.Combine(outside, "canary.txt");
        var planted = Path.Combine(outside, "planted.txt");
        File.WriteAllText(canary, "provider-outside-secret");
        try
        {
            var p = RealHardened(
                $"cat '{canary}'; echo cat=$?; echo x > '{planted}'; echo write=$?; echo ok > ./inside.txt && echo inside=ok; cat ./inside.txt");
            RequireHardened(p);

            await using var process = (LinuxSandboxProcess)await p.StartAsync(Spec(), TestContext.Current.CancellationToken);
            await process.WaitForExitAsync(TestContext.Current.CancellationToken);

            Assert.DoesNotContain("provider-outside-secret", process.Stdout, StringComparison.Ordinal);
            Assert.DoesNotContain("cat=0", process.Stdout, StringComparison.Ordinal);
            Assert.DoesNotContain("write=0", process.Stdout, StringComparison.Ordinal);
            Assert.Contains("inside=ok", process.Stdout, StringComparison.Ordinal);
            Assert.False(File.Exists(planted));
        }
        finally
        {
            Directory.Delete(outside, recursive: true);
        }
    }

    [Fact]
    public async Task The_policy_file_is_gone_and_the_work_directory_is_empty_when_the_module_runs()
    {
        var p = RealHardened("ls -A");
        RequireHardened(p);

        await using var process = (LinuxSandboxProcess)await p.StartAsync(Spec(), TestContext.Current.CancellationToken);
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, process.ExitCode);
        Assert.Equal(string.Empty, process.Stdout.Trim());
    }

    [Fact]
    public async Task A_real_hardened_run_that_calls_socket_is_killed_by_seccomp()
    {
        Assert.SkipUnless(File.Exists("/usr/bin/bash"), "bash not installed: cannot make the module call socket()");
        var p = new LinuxConfinementProvider(HardenedOptions(), new BashPlan("exec 3<>/dev/tcp/127.0.0.1/9; echo after-socket"));
        RequireHardened(p);

        await using var process = (LinuxSandboxProcess)await p.StartAsync(Spec(), TestContext.Current.CancellationToken);
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);

        Assert.Equal(128 + 31, process.ExitCode);
        Assert.DoesNotContain("after-socket", process.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Start_cleans_up_and_still_gives_a_private_work_directory_with_hardening_on()
    {
        var p = RealHardened("true");
        RequireHardened(p);

        var process = (LinuxSandboxProcess)await p.StartAsync(Spec(), TestContext.Current.CancellationToken);
        var dir = process.WorkDirectory;
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(dir));
        await process.DisposeAsync();
        Assert.False(Directory.Exists(dir));
    }

    private sealed class BashPlan(string script) : ILinuxSandboxLaunchPlan
    {
        public LinuxLaunchCommand Create(SandboxRunSpec spec, string workDirectory) => new("/usr/bin/bash", ["-c", script]);
    }
}
