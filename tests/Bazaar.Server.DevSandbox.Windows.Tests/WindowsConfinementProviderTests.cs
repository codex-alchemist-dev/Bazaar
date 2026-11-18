using Bazaar.Server.DevSandbox.Windows;

namespace Bazaar.Server.DevSandbox.Windows.Tests;

public sealed class WindowsConfinementProviderTests : IDisposable
{
    private const string _exe = "C:\\Tools\\module.exe";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "bazaar-win-tests-" + Guid.NewGuid().ToString("N"));

    public WindowsConfinementProviderTests()
    {
        Directory.CreateDirectory(_root);
    }

    private sealed class Plan(Func<SandboxRunSpec, string, WindowsLaunchCommand>? create = null) : IWindowsSandboxLaunchPlan
    {
        public WindowsLaunchCommand Create(SandboxRunSpec spec, string workDirectory) =>
            create is null ? new WindowsLaunchCommand(_exe, ["--run", "a b"]) : create(spec, workDirectory);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }

    private WindowsConfinementOptions Options() => new()
    {
        WorkRoot = _root,
        SystemDirectory = "C:\\Windows\\System32",
        SystemRoot = "C:\\Windows",
        SelfTestTimeout = TimeSpan.FromMilliseconds(600),
        MaxProcesses = 16,
    };

    private static SandboxRunSpec Spec(string runId = "run-1", int memoryMb = 128, int timeoutSeconds = 30) =>
        new(runId, "mod", "1.0.0", SandboxEngineKind.Vm, [], [], 1UL, timeoutSeconds, memoryMb);

    private WindowsConfinementProvider Provider(FakeWindowsApi api, Plan? plan = null) => new(Options(), plan ?? new Plan(), api);

    private static string[] RunCalls(FakeWindowsApi api, int skip) => api.Calls.Skip(skip).ToArray();

    private static async Task<(WindowsConfinementProvider Provider, FakeWindowsApi Api, int Mark)> Ready(WindowsConfinementProviderTests t, FakeWindowsApi? api = null, Plan? plan = null)
    {
        api ??= new FakeWindowsApi();
        var p = t.Provider(api, plan);
        Assert.True(p.Tested, p.UntestedReason);
        await Task.CompletedTask;
        return (p, api, api.Calls.Count);
    }

    // ---- provider basics ----

    [Fact]
    public void Os_is_windows()
    {
        Assert.Equal(SandboxOs.Windows, Provider(new FakeWindowsApi()).Os);
    }

    [Fact]
    public void A_clean_fake_host_passes_the_self_test_with_the_appcontainer_level()
    {
        var p = Provider(new FakeWindowsApi());
        Assert.True(p.Tested, p.UntestedReason);
        Assert.Null(p.UntestedReason);
        Assert.Equal(WindowsConfinementSelfTest.AppContainerLevel, p.HardeningLevel);
        Assert.Empty(p.SelfTestResult.Reasons);
    }

    [Fact]
    public async Task A_non_windows_host_is_untested_never_throws_and_refuses_to_start()
    {
        var p = Provider(new FakeWindowsApi { OnWindows = false });
        Assert.False(p.Tested);
        Assert.Contains("not Windows", p.UntestedReason, StringComparison.Ordinal);
        Assert.Equal(WindowsConfinementSelfTest.NoLevel, p.HardeningLevel);
        await Assert.ThrowsAsync<InvalidOperationException>(() => p.StartAsync(Spec(), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Without_any_api_the_provider_is_untested()
    {
        var p = new WindowsConfinementProvider(Options(), new Plan(), null);
        Assert.False(p.Tested);
        await Assert.ThrowsAsync<InvalidOperationException>(() => p.StartAsync(Spec(), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task The_public_constructor_on_this_host_is_consistent_and_never_throws()
    {
        var p = new WindowsConfinementProvider(new WindowsConfinementOptions { WorkRoot = _root }, new Plan());
        Assert.Equal(p.Tested, p.UntestedReason is null);
        if (!OperatingSystem.IsWindows())
        {
            Assert.False(p.Tested);
            Assert.Equal(WindowsConfinementSelfTest.NoLevel, p.HardeningLevel);
        }

        var direct = await WindowsConfinementSelfTest.RunAsync(new WindowsConfinementOptions { WorkRoot = _root }, TestContext.Current.CancellationToken);
        Assert.Equal(p.Tested, direct.Passed);
    }

    // ---- exact call order ----

    [Fact]
    public async Task Start_calls_the_api_in_the_exact_order_and_never_resumes_before_assigning()
    {
        var (p, api, mark) = await Ready(this);
        await using var process = (WindowsSandboxProcess)await p.StartAsync(Spec(), TestContext.Current.CancellationToken);

        Assert.Equal(
            ["CreateJob", "CreateAppContainerProfile", "GrantWorkDirectoryAccess", "CreateProcessSuspended", "Assign", "Resume"],
            RunCalls(api, mark));
        Assert.True(Directory.Exists(process.WorkDirectory));
    }

    [Fact]
    public async Task Start_builds_the_request_from_the_spec_and_the_plan()
    {
        var (p, api, mark) = await Ready(this);
        await using var process = (WindowsSandboxProcess)await p.StartAsync(Spec(runId: "abc-9", memoryMb: 64, timeoutSeconds: 45), TestContext.Current.CancellationToken);

        var request = api.Requests[^1];
        var job = api.Jobs[^1].QueryLimits();
        Assert.Equal(WindowsConfinementMode.AppContainer, request.Mode);
        Assert.Equal("S-1-15-2-1-2-3-4-5-6-7", request.AppContainerSid);
        Assert.Equal(_exe, request.ApplicationPath);
        Assert.Equal("\"C:\\Tools\\module.exe\" --run \"a b\"", request.CommandLine);
        Assert.Equal(process.WorkDirectory, request.WorkingDirectory);
        Assert.StartsWith(Path.Combine(_root, "bazaar-sbx-abc-9-"), process.WorkDirectory, StringComparison.Ordinal);
        Assert.Equal($"SystemRoot=C:\\Windows\0TEMP={process.WorkDirectory}\0TMP={process.WorkDirectory}\0\0", request.EnvironmentBlock);
        Assert.Equal(64L * 1024 * 1024, job.ProcessMemoryBytes);
        Assert.Equal(64L * 1024 * 1024, job.JobMemoryBytes);
        Assert.Equal(TimeSpan.FromSeconds(45), job.CpuTime);
        Assert.Equal(16, job.ActiveProcessLimit);
        Assert.True(job.KillOnJobClose);
        Assert.False(job.BreakawayAllowed);
        Assert.Equal(0xFFu, job.UiRestrictions);
        Assert.True(api.Processes[^1].Resumed);
        Assert.True(mark > 0);
    }

    [Fact]
    public async Task Profile_names_are_valid_unique_and_at_most_64_characters()
    {
        var (p, api, _) = await Ready(this);
        var longRun = new string('x', 80);
        await using var a = await p.StartAsync(Spec(runId: longRun), TestContext.Current.CancellationToken);
        await using var b = await p.StartAsync(Spec(runId: longRun), TestContext.Current.CancellationToken);

        var names = new List<string>();
        await a.DisposeAsync();
        await b.DisposeAsync();
        names.AddRange(api.DeletedProfiles.TakeLast(2));
        Assert.Equal(2, names.Distinct().Count());
        Assert.All(names, n =>
        {
            Assert.True(n.Length <= 64, n);
            Assert.Matches("^[a-z0-9.-]+$", n);
            Assert.StartsWith("bazaar.sbx.", n, StringComparison.Ordinal);
        });
    }

    // ---- validation ----

    [Theory]
    [InlineData("Bad_Id")]
    [InlineData("UPPER")]
    [InlineData("has space")]
    [InlineData("")]
    public async Task A_bad_run_id_is_refused_before_anything_is_created(string runId)
    {
        var (p, api, mark) = await Ready(this);
        await Assert.ThrowsAsync<ArgumentException>(() => p.StartAsync(Spec(runId: runId), TestContext.Current.CancellationToken));
        Assert.Empty(RunCalls(api, mark));
    }

    [Theory]
    [InlineData(0, 30)]
    [InlineData(-1, 30)]
    [InlineData(128, 0)]
    public async Task Non_positive_limits_are_refused(int memoryMb, int timeoutSeconds)
    {
        var (p, api, mark) = await Ready(this);
        await Assert.ThrowsAsync<ArgumentException>(() => p.StartAsync(Spec(memoryMb: memoryMb, timeoutSeconds: timeoutSeconds), TestContext.Current.CancellationToken));
        Assert.Empty(RunCalls(api, mark));
    }

    [Fact]
    public async Task A_cancelled_token_starts_nothing()
    {
        var (p, api, mark) = await Ready(this);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => p.StartAsync(Spec(), cts.Token));
        Assert.Empty(RunCalls(api, mark));
    }

    [Theory]
    [InlineData("relative\\x.exe")]
    [InlineData("\\\\server\\share\\x.exe")]
    [InlineData("C:x.exe")]
    [InlineData("C:\\a\\..\\x.exe")]
    [InlineData("C:\\a\\x.bat")]
    [InlineData("C:\\a\\x.CMD")]
    [InlineData("C:\\Windows\\System32\\cmd.exe")]
    [InlineData("C:\\missing\\x.exe")]
    public async Task An_unacceptable_executable_is_refused_and_everything_is_cleaned_up(string executable)
    {
        var api = new FakeWindowsApi();
        api.MissingFiles.Add("C:\\missing\\x.exe");
        var (p, _, mark) = await Ready(this, api, new Plan((_, _) => new WindowsLaunchCommand(executable, [])));

        await Assert.ThrowsAsync<InvalidOperationException>(() => p.StartAsync(Spec(), TestContext.Current.CancellationToken));

        var calls = RunCalls(api, mark);
        Assert.DoesNotContain("CreateProcessSuspended", calls);
        Assert.Contains("DeleteAppContainerProfile", calls);
        Assert.True(api.Jobs[^1].Disposed);
        Assert.Empty(Directory.GetDirectories(_root));
    }

    // ---- cleanup at every failure point ----

    public static TheoryData<string> FailurePoints =>
        ["CreateJob", "CreateAppContainerProfile", "GrantWorkDirectoryAccess", "plan", "CreateProcessSuspended", "Assign", "Resume"];

    [Theory]
    [MemberData(nameof(FailurePoints))]
    public async Task A_failure_at_any_step_tears_down_everything_created_so_far(string failAt)
    {
        var api = new FakeWindowsApi();
        var plan = new Plan((_, _) => failAt == "plan" ? throw new InvalidOperationException("plan failed") : new WindowsLaunchCommand(_exe, []));
        var (p, _, mark) = await Ready(this, api, plan);
        api.FailAt = failAt;
        var jobsBefore = api.Jobs.Count;

        await Assert.ThrowsAsync<InvalidOperationException>(() => p.StartAsync(Spec(), TestContext.Current.CancellationToken));

        var newJobs = api.Jobs.Skip(jobsBefore).ToList();
        var calls = RunCalls(api, mark);
        var profileCreated = calls.Contains("CreateAppContainerProfile") && failAt != "CreateAppContainerProfile" && failAt != "CreateJob";
        var processCreated = calls.Contains("CreateProcessSuspended") && failAt != "CreateProcessSuspended";

        // The work directory is gone.
        Assert.Empty(Directory.GetDirectories(_root));

        // The profile is deleted exactly when one was created.
        Assert.Equal(profileCreated, calls.Contains("DeleteAppContainerProfile"));

        // The job (kill-on-close) is closed whenever it was created.
        if (failAt == "CreateJob")
        {
            Assert.Empty(newJobs);
        }
        else
        {
            Assert.True(Assert.Single(newJobs).Disposed);
        }

        // A created process is terminated and disposed.
        if (processCreated)
        {
            var process = api.Processes[^1];
            Assert.True(process.Terminated);
            Assert.True(process.Disposed);
            Assert.True(process.HasExited);
        }

        // A process is never resumed unless it was assigned first.
        if (failAt is "CreateProcessSuspended" or "Assign" or "CreateJob" or "CreateAppContainerProfile" or "GrantWorkDirectoryAccess" or "plan")
        {
            Assert.DoesNotContain("Resume", calls);
        }

        // Order of what did happen is still the documented one.
        var expected = new[] { "CreateJob", "CreateAppContainerProfile", "GrantWorkDirectoryAccess", "CreateProcessSuspended", "Assign", "Resume" };
        var seen = calls.Where(expected.Contains).ToArray();
        Assert.Equal(expected.Where(seen.Contains).ToArray(), seen);
    }

    // ---- kill and dispose ----

    [Fact]
    public async Task Kill_terminates_the_job_and_the_process_has_exited()
    {
        var plan = new Plan((_, _) => new WindowsLaunchCommand("C:\\Windows\\System32\\cmd.exe", []));
        var api = new FakeWindowsApi();
        var p = Provider(api, plan);
        Assert.True(p.Tested);

        // Plan commands may not be a shell, so launch the long-running probe through the internal launcher as the self-test does.
        await using var process = WindowsLauncher.Launch(
            api,
            Options(),
            "kill-test",
            new WindowsJobSpec(1 << 28, 1 << 28, TimeSpan.FromSeconds(60), 8),
            WindowsConfinementMode.AppContainer,
            work =>
            {
                File.WriteAllText(Path.Combine(work, "spin.cmd"), "x");
                return new WindowsLaunchCommand("C:\\Windows\\System32\\cmd.exe", ["/d", "/c", "spin.cmd"]);
            },
            allowShell: true);

        Assert.False(process.HasExited);
        await process.KillAsync(TestContext.Current.CancellationToken);

        Assert.True(process.HasExited);
        Assert.Equal(1, process.ExitCode);
        Assert.True(api.Jobs[^1].Terminated);
    }

    [Fact]
    public async Task Dispose_is_idempotent_and_cleans_up_the_job_the_profile_and_the_directory()
    {
        var (p, api, mark) = await Ready(this);
        var process = (WindowsSandboxProcess)await p.StartAsync(Spec(), TestContext.Current.CancellationToken);
        var dir = process.WorkDirectory;

        await process.DisposeAsync();
        var after = api.Calls.Count;
        await process.DisposeAsync();
        await process.KillAsync(TestContext.Current.CancellationToken);

        Assert.Equal(after, api.Calls.Count);
        Assert.False(Directory.Exists(dir));
        Assert.True(api.Jobs[^1].Disposed);
        Assert.Contains("TerminateJob", RunCalls(api, mark));
        Assert.Contains("DeleteAppContainerProfile", RunCalls(api, mark));
        Assert.True(api.Processes[^1].Disposed);
    }

    [Fact]
    public async Task Dispose_still_cleans_up_when_terminating_the_job_throws()
    {
        var (p, api, _) = await Ready(this);
        var process = (WindowsSandboxProcess)await p.StartAsync(Spec(), TestContext.Current.CancellationToken);
        var dir = process.WorkDirectory;
        api.FailAt = "TerminateJob";

        await process.DisposeAsync();

        Assert.False(Directory.Exists(dir));
        Assert.True(api.Jobs[^1].Disposed);
        Assert.True(api.Processes[^1].Terminated);
        Assert.NotEmpty(api.DeletedProfiles);
    }

    [Fact]
    public async Task Output_is_captured()
    {
        var (p, _, _) = await Ready(this);
        await using var process = (WindowsSandboxProcess)await p.StartAsync(Spec(), TestContext.Current.CancellationToken);
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);
        Assert.Equal(0, process.ExitCode);
        Assert.Equal("hello\n", process.Stdout);
    }

    // ---- self-test: each failed probe means untested ----

    public static TheoryData<string, string> FailingProbes => new()
    {
        { nameof(FakeBehavior.KillOnCloseWorks), "(a) closing the job object did not kill its process" },
        { nameof(FakeBehavior.KillOnCloseFlag), "(a) the job does not have kill-on-close set" },
        { nameof(FakeBehavior.BreakawayAllowed), "(b) the job allows breakaway" },
        { nameof(FakeBehavior.ChildSpawns), "(b) a child process of the job was not found" },
        { nameof(FakeBehavior.ProcessMemoryOverride), "(e) memory limit not applied" },
        { nameof(FakeBehavior.ActiveProcessOverride), "(e) active process limit" },
        { nameof(FakeBehavior.UiOverride), "(e) UI restrictions" },
        { nameof(FakeBehavior.ReadOutside), "(c) the AppContainer process could read a file outside" },
        { nameof(FakeBehavior.WriteOutside), "(c) the AppContainer process could write a file outside" },
        { nameof(FakeBehavior.WorkdirWritable), "(c) the AppContainer process could not write inside its own work directory" },
        { nameof(FakeBehavior.ScriptRuns), "(c) file probe script did not run" },
        { nameof(FakeBehavior.NetworkConnects), "(d) the AppContainer process opened a network connection" },
        { nameof(FakeBehavior.CurlExit), "(d) network probe ended with exit code 0" },
        { nameof(FakeBehavior.SpinExitsEarly), "job probe exited early" },
    };

    [Theory]
    [MemberData(nameof(FailingProbes))]
    public void Each_failed_probe_makes_the_provider_untested_with_its_reason(string knob, string expectedReason)
    {
        var b = new FakeBehavior();
        switch (knob)
        {
            case nameof(FakeBehavior.KillOnCloseWorks): b.KillOnCloseWorks = false; break;
            case nameof(FakeBehavior.KillOnCloseFlag): b.KillOnCloseFlag = false; break;
            case nameof(FakeBehavior.BreakawayAllowed): b.BreakawayAllowed = true; break;
            case nameof(FakeBehavior.ChildSpawns): b.ChildSpawns = false; break;
            case nameof(FakeBehavior.ProcessMemoryOverride): b.ProcessMemoryOverride = 1; break;
            case nameof(FakeBehavior.ActiveProcessOverride): b.ActiveProcessOverride = 9999; break;
            case nameof(FakeBehavior.UiOverride): b.UiOverride = 0x1; break;
            case nameof(FakeBehavior.ReadOutside): b.ReadOutside = true; break;
            case nameof(FakeBehavior.WriteOutside): b.WriteOutside = true; break;
            case nameof(FakeBehavior.WorkdirWritable): b.WorkdirWritable = false; break;
            case nameof(FakeBehavior.ScriptRuns): b.ScriptRuns = false; break;
            case nameof(FakeBehavior.NetworkConnects): b.NetworkConnects = true; break;
            case nameof(FakeBehavior.CurlExit): b.CurlExit = 0; break;
            case nameof(FakeBehavior.SpinExitsEarly): b.SpinExitsEarly = true; break;
            default: throw new InvalidOperationException(knob);
        }

        var api = new FakeWindowsApi(b);
        var p = Provider(api);

        Assert.False(p.Tested);
        Assert.Contains(expectedReason, p.UntestedReason, StringComparison.Ordinal);
        Assert.Equal(WindowsConfinementSelfTest.NoLevel, p.HardeningLevel);
        Assert.Empty(Directory.GetDirectories(_root));
        Assert.All(api.Jobs, j => Assert.True(j.Disposed));
        Assert.All(api.Processes, pr => Assert.True(pr.Disposed));
    }

    [Fact]
    public async Task An_untested_provider_refuses_to_start_anything()
    {
        var api = new FakeWindowsApi(new FakeBehavior { ReadOutside = true });
        var p = Provider(api);
        var before = api.Calls.Count;
        Assert.False(p.Tested);
        before = api.Calls.Count;

        await Assert.ThrowsAsync<InvalidOperationException>(() => p.StartAsync(Spec(), TestContext.Current.CancellationToken));
        Assert.Equal(before, api.Calls.Count);
    }

    [Fact]
    public void Without_appcontainer_the_fallback_reports_its_weaker_level_and_is_never_tested()
    {
        var api = new FakeWindowsApi(new FakeBehavior { AppContainerSupported = false });
        var p = Provider(api);

        Assert.False(p.Tested);
        Assert.Equal(WindowsConfinementSelfTest.RestrictedTokenLevel, p.HardeningLevel);
        Assert.Contains("AppContainer is not available", p.UntestedReason, StringComparison.Ordinal);
        Assert.DoesNotContain("CreateAppContainerProfile", api.Calls);
        Assert.All(api.Requests, r => Assert.Equal(WindowsConfinementMode.RestrictedToken, r.Mode));
        Assert.All(api.Requests, r => Assert.Null(r.AppContainerSid));
    }

    [Fact]
    public void Without_appcontainer_and_with_a_failing_job_probe_the_level_is_none()
    {
        var api = new FakeWindowsApi(new FakeBehavior { AppContainerSupported = false, KillOnCloseWorks = false });
        var p = Provider(api);
        Assert.False(p.Tested);
        Assert.Equal(WindowsConfinementSelfTest.NoLevel, p.HardeningLevel);
    }

    [Theory]
    [InlineData("C:\\Windows\\System32\\cmd.exe", "cmd.exe not found")]
    [InlineData("C:\\Windows\\System32\\curl.exe", "curl.exe not found")]
    public void A_missing_tool_makes_the_provider_untested(string missing, string reason)
    {
        var api = new FakeWindowsApi();
        api.MissingFiles.Add(missing);
        var p = Provider(api);
        Assert.False(p.Tested);
        Assert.Contains(reason, p.UntestedReason, StringComparison.Ordinal);
        Assert.Empty(api.Calls);
    }

    [Fact]
    public void An_api_that_throws_during_the_self_test_makes_the_provider_untested_without_throwing()
    {
        foreach (var failAt in new[] { "CreateJob", "CreateAppContainerProfile", "GrantWorkDirectoryAccess", "CreateProcessSuspended", "Assign", "Resume" })
        {
            var api = new FakeWindowsApi { FailAt = failAt };
            var p = Provider(api);
            Assert.False(p.Tested);
            Assert.NotNull(p.UntestedReason);
            Assert.Empty(Directory.GetDirectories(_root));
        }
    }

    [Fact]
    public void The_self_test_creates_a_job_profile_and_directory_for_each_probe_and_deletes_them()
    {
        var api = new FakeWindowsApi();
        var p = Provider(api);
        Assert.True(p.Tested, p.UntestedReason);

        Assert.Equal(3, api.Jobs.Count);
        Assert.Equal(3, api.DeletedProfiles.Count);
        Assert.All(api.Jobs, j => Assert.True(j.Disposed));
        Assert.Empty(Directory.GetDirectories(_root));
    }
}
