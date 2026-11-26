namespace Bazaar.Server.WhatIf.Tests;

public sealed class WhatIfEngineTests
{
    private static readonly Guid _gc = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private static WhatIfRequest Request(MetricSpec? spec = null, string? expected = null) => new(
        "council-vote-7", _gc, 42, spec ?? MetricSpec.Default,
        [new WhatIfBranch("keep", ["refactor a"]), new WhatIfBranch("change", ["refactor b", "add tests"])], expected);

    private sealed record Rig(WhatIfEngine Engine, FakeSandboxRunner Runner, InMemoryWhatIfRunStore Store, FakeWhatIfForkSource Forks, InMemoryWhatIfReportSink Sink);

    private static Rig Make(decimal weight = 0)
    {
        var runner = new FakeSandboxRunner();
        var store = new InMemoryWhatIfRunStore();
        var forks = new FakeWhatIfForkSource();
        var sink = new InMemoryWhatIfReportSink();
        return new Rig(new WhatIfEngine(runner, store, forks, sink, new WhatIfSettings(weight)), runner, store, forks, sink);
    }

    [Fact]
    public async Task A_changed_metric_spec_aborts_before_any_branch_runs()
    {
        var rig = Make();
        var original = MetricSpec.Default.Hash();
        var changed = MetricSpec.Default with { DiffSizeCapLines = 10 };

        var result = await rig.Engine.RunAsync(Request(changed, original), TestContext.Current.CancellationToken);

        Assert.Equal(WhatIfRunState.Aborted, result.State);
        Assert.Empty(rig.Runner.Requests);
        Assert.Empty(rig.Forks.Forks);
        var stored = await rig.Store.GetAsync(result.RunId, TestContext.Current.CancellationToken);
        Assert.Equal(WhatIfRunState.Aborted, stored!.State);
        Assert.Contains("hash mismatch", stored.AbortReason);
    }

    [Fact]
    public async Task A_runner_that_scored_on_another_spec_aborts_the_run()
    {
        var rig = Make();
        rig.Runner.ReportedSpecHash = new string('a', 64);

        var result = await rig.Engine.RunAsync(Request(), TestContext.Current.CancellationToken);

        Assert.Equal(WhatIfRunState.Aborted, result.State);
        Assert.Empty(result.Ranking);
        Assert.Empty(rig.Sink.Entries);
    }

    [Fact]
    public async Task A_matching_spec_runs_every_branch_on_its_own_task_list_and_ranks_them()
    {
        var rig = Make();
        var spec = MetricSpec.Default;

        var result = await rig.Engine.RunAsync(Request(spec, spec.Hash()), TestContext.Current.CancellationToken);

        Assert.Equal(WhatIfRunState.Completed, result.State);
        Assert.Equal(2, result.Ranking.Count);
        Assert.True(result.Ranking[0].Score >= result.Ranking[1].Score);
        Assert.Equal(["refactor a"], rig.Runner.Requests.Single(r => r.Branch == "keep").Tasks);
        Assert.Equal(["refactor b", "add tests"], rig.Runner.Requests.Single(r => r.Branch == "change").Tasks);
        Assert.All(rig.Runner.Requests, r =>
        {
            Assert.Equal(spec.Hash(), r.MetricsSpecHash);
            Assert.StartsWith("sandbox/", r.PushRef, StringComparison.Ordinal);
            Assert.Equal(SandboxEgress.Denied, r.Profile.Egress);
        });
        Assert.Single(rig.Sink.Entries);
        Assert.Equal(_gc, rig.Sink.Entries[0].GcId);
    }

    [Fact]
    public async Task The_report_states_the_counterfactual_limit()
    {
        var rig = Make();

        var result = await rig.Engine.RunAsync(Request(), TestContext.Current.CancellationToken);

        Assert.Contains("other agents", result.Limitation);
        Assert.Contains("owner", result.Limitation);
        Assert.EndsWith(WhatIfLimits.Counterfactual, result.Report, StringComparison.Ordinal);
        Assert.EndsWith(WhatIfLimits.Counterfactual, rig.Sink.Entries[0].Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_failed_runner_marks_the_run_failed_and_still_states_the_limit()
    {
        var rig = Make();
        rig.Runner.FailWith = new TimeoutException("vm");

        var result = await rig.Engine.RunAsync(Request(), TestContext.Current.CancellationToken);

        Assert.Equal(WhatIfRunState.Failed, result.State);
        Assert.Contains(WhatIfLimits.Counterfactual, result.Report);
    }

    [Fact]
    public async Task The_same_request_gives_the_same_scores_twice()
    {
        var first = await Make().Engine.RunAsync(Request(), TestContext.Current.CancellationToken);
        var second = await Make().Engine.RunAsync(Request(), TestContext.Current.CancellationToken);

        Assert.Equal(first.Ranking, second.Ranking);
    }

    [Fact]
    public async Task The_result_carries_the_reputation_weight_default_zero()
    {
        Assert.Equal(0m, (await Make().Engine.RunAsync(Request(), TestContext.Current.CancellationToken)).ReputationWeight);
        Assert.Equal(0.25m, (await Make(0.25m).Engine.RunAsync(Request(), TestContext.Current.CancellationToken)).ReputationWeight);
    }

    [Fact]
    public async Task A_request_with_one_branch_or_a_bad_branch_name_is_refused()
    {
        var rig = Make();
        var ct = TestContext.Current.CancellationToken;

        await Assert.ThrowsAsync<ArgumentException>(() => rig.Engine.RunAsync(Request() with { Branches = [new WhatIfBranch("only", ["t"])] }, ct));
        await Assert.ThrowsAsync<ArgumentException>(() => rig.Engine.RunAsync(
            Request() with { Branches = [new WhatIfBranch("a/../b", ["t"]), new WhatIfBranch("c", ["t"])] }, ct));
    }

    private static WhatIfRequest ThreeBranches() => new(
        "council-vote-8", _gc, 42, MetricSpec.Default,
        [new WhatIfBranch("keep", ["a"]), new WhatIfBranch("change", ["b"]), new WhatIfBranch("drop", ["c"])]);

    [Fact]
    public async Task A_quota_refusal_aborts_only_that_branch_and_the_others_are_scored()
    {
        var rig = Make();
        rig.Runner.QuotaRefusals["change"] = SandboxQuotaRefusal.QuotaExhausted;

        var result = await rig.Engine.RunAsync(ThreeBranches(), TestContext.Current.CancellationToken);

        Assert.Equal(WhatIfRunState.Completed, result.State);
        Assert.Equal(["drop", "keep"], result.Ranking.Select(r => r.Branch).Order(StringComparer.Ordinal));
        Assert.DoesNotContain(result.Ranking, r => r.Branch == "change");
        Assert.Equal(3, rig.Runner.Requests.Count);
        var stored = await rig.Store.GetAsync(result.RunId, TestContext.Current.CancellationToken);
        var refused = stored!.Branches.Single(b => b.Name == "change");
        Assert.Equal(WhatIfRunState.Aborted, refused.State);
        Assert.Contains("QuotaExhausted", refused.AbortReason);
        Assert.Null(refused.Result);
        Assert.All(stored.Branches.Where(b => b.Name != "change"), b => Assert.Equal(WhatIfRunState.Completed, b.State));
        Assert.Contains("- change: Aborted", result.Report);
        Assert.Single(rig.Sink.Entries);
    }

    [Fact]
    public async Task A_window_refusal_is_a_branch_abort_too_and_all_refused_leaves_an_aborted_run_with_no_ranking()
    {
        var rig = Make();
        rig.Runner.QuotaRefusals["keep"] = SandboxQuotaRefusal.OutsideWindow;
        rig.Runner.QuotaRefusals["change"] = SandboxQuotaRefusal.OutsideWindow;

        var result = await rig.Engine.RunAsync(Request(), TestContext.Current.CancellationToken);

        Assert.Equal(WhatIfRunState.Aborted, result.State);
        Assert.Empty(result.Ranking);
        Assert.Contains("- keep: Aborted", result.Report);
        Assert.Contains("OutsideWindow", result.Report);
    }

    private sealed class RejectingRunner(string rejectBranch) : ISandboxRunner
    {
        public Task<SandboxOutcome> RunAsync(SandboxRequest request, CancellationToken ct = default) =>
            request.Branch == rejectBranch
                ? Task.FromException<SandboxOutcome>(new SandboxRequestRejectedException(["egress must be default-deny"]))
                : new FakeSandboxRunner().RunAsync(request, ct);
    }

    [Fact]
    public async Task A_guard_violation_still_aborts_the_whole_run_including_scored_branches()
    {
        var store = new InMemoryWhatIfRunStore();
        var sink = new InMemoryWhatIfReportSink();
        var engine = new WhatIfEngine(new RejectingRunner("change"), store, new FakeWhatIfForkSource(), sink, new WhatIfSettings(0));

        var result = await engine.RunAsync(ThreeBranches(), TestContext.Current.CancellationToken);

        Assert.Equal(WhatIfRunState.Aborted, result.State);
        Assert.Empty(result.Ranking);
        Assert.Empty(sink.Entries);
        var stored = await store.GetAsync(result.RunId, TestContext.Current.CancellationToken);
        Assert.All(stored!.Branches, b =>
        {
            Assert.Equal(WhatIfRunState.Aborted, b.State);
            Assert.Null(b.Result);
        });
    }

    [Fact]
    public void A_quota_refusal_is_not_a_guard_violation_type()
    {
        Assert.False(typeof(SandboxQuotaRefusedException).IsAssignableTo(typeof(SandboxRequestRejectedException)));
        Assert.False(typeof(SandboxRequestRejectedException).IsAssignableTo(typeof(SandboxQuotaRefusedException)));
    }
}
