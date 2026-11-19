using Microsoft.Extensions.DependencyInjection;

namespace Bazaar.Server.WhatIf.Tests;

public sealed class VmSandboxRunnerTests
{
    private static readonly Guid _gc = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly string _hash = MetricSpec.Default.Hash();

    private sealed record Rig(VmSandboxRunner Runner, FakeSandboxVmHost Host, FakeSandboxPushGate Gate, FakeSandboxQuota Quota);

    private static Rig Make()
    {
        var host = new FakeSandboxVmHost();
        var gate = new FakeSandboxPushGate();
        var quota = new FakeSandboxQuota();
        return new Rig(new VmSandboxRunner(host, gate, quota), host, gate, quota);
    }

    private static SandboxRequest Request(
        string pushRef = "sandbox/what-if/r1/keep",
        SandboxProfile? profile = null,
        WorkLifeWindow window = WorkLifeWindow.Break,
        IReadOnlyList<string>? tasks = null) => new(
            Guid.CreateVersion7(), "keep", _gc, tasks ?? ["one", "two", "three"], _hash, profile ?? SandboxProfile.Default, pushRef, window);

    [Fact]
    public async Task A_good_run_executes_tasks_in_order_pushes_a_sandbox_ref_and_tears_down()
    {
        var rig = Make();
        var request = Request();

        var outcome = await rig.Runner.RunAsync(request, TestContext.Current.CancellationToken);

        var session = Assert.Single(rig.Host.Sessions);
        Assert.Equal(["one", "two", "three"], session.Tasks);
        Assert.Equal(request.PushRef, outcome.PushedRef);
        Assert.Equal(_hash, outcome.MetricsSpecHash);
        Assert.Equal(rig.Host.Measurement, outcome.Measurement);
        Assert.Single(rig.Gate.Pushes);
        Assert.Equal(1, session.TornDown);
    }

    [Fact]
    public async Task Guard_rejection_happens_before_quota_or_provisioning()
    {
        var rig = Make();

        await Assert.ThrowsAsync<SandboxRequestRejectedException>(() =>
            rig.Runner.RunAsync(Request(profile: new SandboxProfile(PassportToken: "x")), TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<SandboxRequestRejectedException>(() =>
            rig.Runner.RunAsync(Request(profile: new SandboxProfile(Egress: SandboxEgress.Open)), TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<SandboxRequestRejectedException>(() =>
            rig.Runner.RunAsync(Request(pushRef: "main"), TestContext.Current.CancellationToken));

        Assert.Empty(rig.Quota.Requests);
        Assert.Empty(rig.Host.Specs);
        Assert.Empty(rig.Gate.Pushes);
    }

    [Fact]
    public async Task The_push_gate_refuses_a_non_sandbox_ref_on_its_own()
    {
        var gate = new FakeSandboxPushGate();

        await Assert.ThrowsAsync<SandboxRequestRejectedException>(() =>
            gate.PushAsync(new SandboxPushRequest(Guid.NewGuid(), "keep", "vm", "agent/task"), TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<SandboxRequestRejectedException>(() =>
            gate.PushAsync(new SandboxPushRequest(Guid.NewGuid(), "keep", "vm", "refs/heads/main"), TestContext.Current.CancellationToken));
        Assert.Empty(gate.Pushes);
    }

    [Fact]
    public async Task A_gate_that_reports_a_ref_outside_sandbox_fails_the_run_and_still_tears_down()
    {
        var rig = Make();
        rig.Gate.ReportedRef = "main";

        var ex = await Assert.ThrowsAsync<SandboxRunFailedException>(() => rig.Runner.RunAsync(Request(), TestContext.Current.CancellationToken));

        Assert.Equal("push", ex.Stage);
        Assert.Equal(1, Assert.Single(rig.Host.Sessions).TornDown);
    }

    [Fact]
    public async Task A_failed_task_tears_down_pushes_nothing_and_leaks_no_host_text()
    {
        var rig = Make();
        rig.Host.TaskBehaviour = task => task == "two"
            ? throw new InvalidOperationException("token ghs_SECRET leaked in message")
            : new SandboxTaskResult(true);

        var ex = await Assert.ThrowsAsync<SandboxRunFailedException>(() => rig.Runner.RunAsync(Request(), TestContext.Current.CancellationToken));

        Assert.DoesNotContain("SECRET", ex.Message);
        Assert.Null(ex.InnerException);
        Assert.Equal("task", ex.Stage);
        var session = Assert.Single(rig.Host.Sessions);
        Assert.Equal(["one", "two"], session.Tasks);
        Assert.Empty(rig.Gate.Pushes);
        Assert.Equal(1, session.TornDown);
    }

    [Fact]
    public async Task A_task_the_harness_could_not_finish_fails_the_run_without_a_push()
    {
        var rig = Make();
        rig.Host.TaskBehaviour = _ => new SandboxTaskResult(false, "tool_denied");

        var ex = await Assert.ThrowsAsync<SandboxRunFailedException>(() => rig.Runner.RunAsync(Request(), TestContext.Current.CancellationToken));

        Assert.Equal("task", ex.Stage);
        Assert.Empty(rig.Gate.Pushes);
        Assert.Equal(1, Assert.Single(rig.Host.Sessions).TornDown);
    }

    [Fact]
    public async Task Provision_failure_maps_to_a_stage_only_failure_and_pushes_nothing()
    {
        var rig = Make();
        rig.Host.ProvisionFailure = new InvalidOperationException("host path C:\\secret");

        var ex = await Assert.ThrowsAsync<SandboxRunFailedException>(() => rig.Runner.RunAsync(Request(), TestContext.Current.CancellationToken));

        Assert.Equal("provision", ex.Stage);
        Assert.DoesNotContain("secret", ex.Message);
        Assert.Empty(rig.Gate.Pushes);
    }

    [Fact]
    public async Task Cancellation_mid_run_still_tears_down_and_pushes_nothing()
    {
        var rig = Make();
        rig.Host.Gate = new TaskCompletionSource();
        using var cts = new CancellationTokenSource();

        var run = rig.Runner.RunAsync(Request(), cts.Token);
        while (rig.Host.Sessions.Count == 0 || rig.Host.Sessions[0].Tasks.Count == 0)
            await Task.Delay(5, TestContext.Current.CancellationToken);
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        Assert.Equal(1, Assert.Single(rig.Host.Sessions).TornDown);
        Assert.Empty(rig.Gate.Pushes);
    }

    [Fact]
    public async Task A_teardown_error_does_not_mask_the_original_failure_but_fails_an_otherwise_good_run()
    {
        var rig = Make();
        rig.Host.TaskBehaviour = _ => new SandboxTaskResult(false);
        // Sessions are created at provision time, so wrap the host to set the failure on creation.
        var failingTeardown = new TeardownFailingHost(rig.Host);
        var runner = new VmSandboxRunner(failingTeardown, rig.Gate, rig.Quota);

        var first = await Assert.ThrowsAsync<SandboxRunFailedException>(() => runner.RunAsync(Request(), TestContext.Current.CancellationToken));
        Assert.Equal("task", first.Stage);

        rig.Host.TaskBehaviour = null;
        var second = await Assert.ThrowsAsync<SandboxRunFailedException>(() => runner.RunAsync(Request(), TestContext.Current.CancellationToken));
        Assert.Equal("teardown", second.Stage);
    }

    [Fact]
    public async Task Teardown_is_idempotent()
    {
        var rig = Make();
        await rig.Runner.RunAsync(Request(), TestContext.Current.CancellationToken);
        var session = Assert.Single(rig.Host.Sessions);

        await session.TeardownAsync(TestContext.Current.CancellationToken);
        await session.TeardownAsync(TestContext.Current.CancellationToken);

        Assert.Equal(3, session.TeardownCalls);
        Assert.Equal(1, session.TornDown);
    }

    [Fact]
    public async Task A_refused_quota_stops_before_provisioning()
    {
        var rig = Make();
        rig.Quota.Remaining = 0;

        var ex = await Assert.ThrowsAsync<SandboxRequestRejectedException>(() => rig.Runner.RunAsync(Request(), TestContext.Current.CancellationToken));

        Assert.Contains("QuotaExhausted", ex.Message);
        Assert.Empty(rig.Host.Specs);
    }

    [Fact]
    public async Task A_window_outside_the_allowed_set_is_refused_and_a_grant_counts_against_quota()
    {
        var rig = Make();
        rig.Quota.AllowedWindows = new HashSet<WorkLifeWindow> { WorkLifeWindow.Off };
        rig.Quota.Remaining = 1;

        var ex = await Assert.ThrowsAsync<SandboxRequestRejectedException>(() =>
            rig.Runner.RunAsync(Request(window: WorkLifeWindow.Break), TestContext.Current.CancellationToken));
        Assert.Contains("OutsideWindow", ex.Message);
        Assert.Empty(rig.Host.Specs);
        Assert.Equal(1, rig.Quota.Remaining);

        await rig.Runner.RunAsync(Request(window: WorkLifeWindow.Off), TestContext.Current.CancellationToken);
        Assert.Equal(0, rig.Quota.Remaining);
        Assert.Equal(WorkLifeWindow.Off, rig.Quota.Requests[^1].Window);
    }

    [Fact]
    public async Task No_token_reaches_the_vm_host_call()
    {
        var rig = Make();

        await rig.Runner.RunAsync(Request(), TestContext.Current.CancellationToken);

        var spec = Assert.Single(rig.Host.Specs);
        Assert.Equal(SandboxEgress.Denied, spec.Egress);
        var names = typeof(SandboxVmSpec).GetProperties().Select(p => p.Name).ToArray();
        Assert.DoesNotContain(names, n => n.Contains("Token", StringComparison.OrdinalIgnoreCase) || n.Contains("Passport", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(typeof(SandboxPushRequest).GetProperties(), p => p.Name.Contains("Token", StringComparison.OrdinalIgnoreCase));
        Assert.All(typeof(SandboxVmSpec).GetProperties().Where(p => p.PropertyType == typeof(string)), p => Assert.NotEqual("Token", p.Name));
    }

    [Fact]
    public async Task Through_the_engine_a_vm_run_is_scored_on_the_fixed_spec_and_pushed_to_sandbox_refs()
    {
        var rig = Make();
        var store = new InMemoryWhatIfRunStore();
        var engine = new WhatIfEngine(rig.Runner, store, new FakeWhatIfForkSource(), new InMemoryWhatIfReportSink(), new WhatIfSettings(0));
        var request = new WhatIfRequest(
            "council-vote-9", _gc, 7, MetricSpec.Default,
            [new WhatIfBranch("keep", ["a"]), new WhatIfBranch("change", ["b", "c"])]);

        var result = await engine.RunAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(WhatIfRunState.Completed, result.State);
        var expected = MetricScorer.Score(MetricSpec.Default, rig.Host.Measurement);
        Assert.All(result.Ranking, r => Assert.Equal(expected, r.Score));
        Assert.Equal(2, rig.Gate.Pushes.Count);
        Assert.All(rig.Gate.Pushes, p => Assert.StartsWith("sandbox/", p.Ref));
        Assert.All(rig.Host.Sessions, s => Assert.Equal(1, s.TornDown));
    }

    [Fact]
    public async Task Through_the_engine_a_quota_refusal_aborts_the_run_before_any_vm()
    {
        var rig = Make();
        rig.Quota.Remaining = 0;
        var engine = new WhatIfEngine(rig.Runner, new InMemoryWhatIfRunStore(), new FakeWhatIfForkSource(), new InMemoryWhatIfReportSink(), new WhatIfSettings(0));

        var result = await engine.RunAsync(
            new WhatIfRequest("d", _gc, 1, MetricSpec.Default, [new WhatIfBranch("a", ["x"]), new WhatIfBranch("b", ["y"])]),
            TestContext.Current.CancellationToken);

        Assert.Equal(WhatIfRunState.Aborted, result.State);
        Assert.Empty(rig.Host.Specs);
    }

    [Fact]
    public void The_vm_runner_is_never_the_default_and_the_engine_fails_closed_without_a_runner()
    {
        var services = new ServiceCollection();
        services.AddBazaarWhatIf();
        services.AddSingleton<IWhatIfForkSource, FakeWhatIfForkSource>();
        services.AddSingleton<IWhatIfReportSink, InMemoryWhatIfReportSink>();
        using var provider = services.BuildServiceProvider();

        Assert.Null(provider.GetService<ISandboxRunner>());
        Assert.Throws<InvalidOperationException>(() => provider.GetRequiredService<WhatIfEngine>());
    }

    [Fact]
    public void The_opt_in_extension_binds_the_vm_runner_once_ports_exist()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ISandboxVmHost, FakeSandboxVmHost>();
        services.AddSingleton<ISandboxPushGate, FakeSandboxPushGate>();
        services.AddSingleton<ISandboxQuota, FakeSandboxQuota>();
        services.AddBazaarWhatIfVmRunner();
        using var provider = services.BuildServiceProvider();

        Assert.IsType<VmSandboxRunner>(provider.GetRequiredService<ISandboxRunner>());
    }

    private sealed class TeardownFailingHost(FakeSandboxVmHost inner) : ISandboxVmHost
    {
        public async Task<ISandboxSession> ProvisionAsync(SandboxVmSpec spec, CancellationToken ct = default)
        {
            var session = (FakeSandboxSession)await inner.ProvisionAsync(spec, ct);
            session.TeardownFailure = new InvalidOperationException("boom");
            return session;
        }
    }
}
