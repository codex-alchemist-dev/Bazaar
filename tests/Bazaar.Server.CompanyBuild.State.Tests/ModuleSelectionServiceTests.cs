using Bazaar.Server.ApprovalQueue;
using static Bazaar.Server.CompanyBuild.State.Tests.FakeCatalogue;

namespace Bazaar.Server.CompanyBuild.State.Tests;

public sealed class ModuleSelectionServiceTests
{
    private static readonly Guid _admin = Guid.NewGuid();

    private readonly InMemoryStateStore _store = new();
    private readonly RecordingAudit _audit = new();
    private readonly GatedExecutor _executor = new();
    private readonly FakeApprovalQueue _queue = new();

    private ModuleSelectionService Service(IModuleApprovalRequirement? requirement = null, bool withExecutor = true, bool withQueue = true) =>
        new(
            _store,
            new FakeCatalogue(M("core"), M("web", "core"), M("mail", "core"), M("ext", "web")),
            _audit,
            TimeProvider.System,
            new CompanyBuildStateOptions("acme"),
            withExecutor ? _executor : null,
            withQueue ? _queue : null,
            requirement);

    [Fact]
    public async Task Catalogue_lists_modules_with_selection_and_revision()
    {
        var svc = Service();
        await svc.CommitAsync(_admin, ["web"], 0);

        var cat = await svc.GetCatalogueAsync();

        Assert.Equal(1, cat.Revision);
        Assert.Equal(["core", "ext", "mail", "web"], cat.Modules.Select(m => m.Id));
        Assert.Equal(["web"], cat.Modules.Where(m => m.Selected).Select(m => m.Id));
    }

    [Fact]
    public async Task Preview_writes_nothing_and_reports_added_dependencies_and_removals()
    {
        var svc = Service();
        await svc.CommitAsync(_admin, ["mail"], 0);

        var preview = await svc.PreviewAsync(["ext"]);

        Assert.True(preview.Valid);
        Assert.Equal(["ext", "web"], preview.Added);
        Assert.Equal(["mail"], preview.Removed);
        Assert.Equal(1, (await _store.GetSelectionAsync()).Revision);
    }

    [Fact]
    public async Task Preview_reports_an_unknown_module_as_a_problem()
    {
        var preview = await Service().PreviewAsync(["nope"]);

        Assert.False(preview.Valid);
        Assert.Equal("nope", Assert.Single(preview.Problems).ModuleId);
    }

    [Fact]
    public async Task Commit_bumps_the_revision_and_audits()
    {
        var result = await Service().CommitAsync(_admin, ["web", "web", "core"], 0);

        Assert.True(result.Committed);
        Assert.Equal(1, result.Revision);
        Assert.Equal(["core", "web"], (await _store.GetSelectionAsync()).SelectedModuleIds);
        var entry = Assert.Single(_audit.Entries);
        Assert.Equal("module.selection.committed", entry.Kind);
        Assert.Equal(_admin.ToString("D"), entry.Actor);
    }

    [Fact]
    public async Task Commit_with_a_stale_revision_is_refused_and_returns_the_current_one()
    {
        var svc = Service();
        await svc.CommitAsync(_admin, ["core"], 0);

        var stale = await svc.CommitAsync(_admin, ["web"], 0);

        Assert.False(stale.Committed);
        Assert.Equal(1, stale.Revision);
        Assert.Equal(["core"], (await _store.GetSelectionAsync()).SelectedModuleIds);
    }

    [Fact]
    public async Task Commit_of_an_unknown_module_is_refused_without_writing()
    {
        var result = await Service().CommitAsync(_admin, ["ghost"], 0);

        Assert.False(result.Committed);
        Assert.Equal(0, (await _store.GetSelectionAsync()).Revision);
        Assert.Empty(_audit.Entries);
    }

    [Fact]
    public async Task Two_commits_at_the_same_revision_let_exactly_one_win()
    {
        var svc = Service();

        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(i => Task.Run(() => svc.CommitAsync(_admin, [i % 2 == 0 ? "core" : "mail"], 0))));

        Assert.Equal(1, results.Count(r => r.Committed));
    }

    [Fact]
    public async Task Rebuild_replays_the_first_result_for_the_same_idempotency_key()
    {
        var svc = Service();
        await svc.CommitAsync(_admin, ["web"], 0);

        var first = await svc.StartRebuildAsync(_admin, "key-1");
        var again = await svc.StartRebuildAsync(_admin, "key-1");

        Assert.True(first.Started);
        Assert.Equal(first, again);
        Assert.Equal(1, _executor.Runs);
        _executor.Finish();
        await svc.DrainAsync();
        Assert.Single(await svc.GetBuildsAsync());
    }

    [Fact]
    public async Task Rebuild_replay_after_completion_still_returns_the_first_build()
    {
        var svc = Service();
        await svc.CommitAsync(_admin, ["web"], 0);
        var first = await svc.StartRebuildAsync(_admin, "key-1");
        _executor.Finish();
        await svc.DrainAsync();

        Assert.Equal(first.BuildId, (await svc.StartRebuildAsync(_admin, "key-1")).BuildId);
    }

    [Fact]
    public async Task A_second_rebuild_while_one_runs_is_refused()
    {
        var svc = Service();
        await svc.CommitAsync(_admin, ["web"], 0);
        await svc.StartRebuildAsync(_admin, "key-1");

        var second = await svc.StartRebuildAsync(_admin, "key-2");

        Assert.False(second.Started);
        Assert.Null(second.BuildId);
        Assert.Contains("already running", second.Reason);
        Assert.Equal(1, _executor.Runs);
        _executor.Finish();
        await svc.DrainAsync();
    }

    [Fact]
    public async Task Concurrent_rebuilds_start_exactly_one_build()
    {
        var svc = Service();
        await svc.CommitAsync(_admin, ["web"], 0);

        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(i => Task.Run(() => svc.StartRebuildAsync(_admin, $"key-{i}"))));

        Assert.Equal(1, results.Count(r => r.Started));
        Assert.Equal(1, _executor.Runs);
        _executor.Finish();
        await svc.DrainAsync();
    }

    [Fact]
    public async Task A_new_rebuild_is_allowed_after_the_previous_one_finished()
    {
        var svc = Service();
        await svc.CommitAsync(_admin, ["web"], 0);
        await svc.StartRebuildAsync(_admin, "key-1");
        _executor.Finish();
        await svc.DrainAsync();

        var next = await new ModuleSelectionService(_store, new FakeCatalogue(M("core"), M("web", "core")), _audit, TimeProvider.System, new CompanyBuildStateOptions(), new GatedExecutor()).StartRebuildAsync(_admin, "key-2");

        Assert.True(next.Started);
    }

    [Fact]
    public async Task Rebuild_runs_the_composed_plan_and_records_success()
    {
        var svc = Service();
        await svc.CommitAsync(_admin, ["ext"], 0);
        var started = await svc.StartRebuildAsync(_admin, "key-1");

        Assert.Equal("running", Assert.Single(await svc.GetBuildsAsync()).State);
        _executor.Finish();
        await svc.DrainAsync();

        var plan = Assert.Single(_executor.Plans);
        Assert.Equal("acme", plan.CompanyId);
        Assert.Equal(["core", "ext", "web"], plan.Modules.Select(m => m.Id).Order());
        var record = Assert.Single(await svc.GetBuildsAsync());
        Assert.Equal(started.BuildId, record.BuildId);
        Assert.Equal("succeeded", record.State);
        Assert.NotNull(record.FinishedAt);
        Assert.Contains(_audit.Entries, e => e.Kind == "module.rebuild.started");
    }

    [Fact]
    public async Task An_incomplete_build_is_recorded_as_failed_and_leaves_the_selection()
    {
        var svc = Service();
        await svc.CommitAsync(_admin, ["web"], 0);
        await svc.StartRebuildAsync(_admin, "key-1");

        _executor.Finish(complete: false);
        await svc.DrainAsync();

        var record = Assert.Single(await svc.GetBuildsAsync());
        Assert.Equal("failed", record.State);
        Assert.Equal(3, record.Attempt);
        Assert.Contains("INCOMPLETE", record.ReportSummary);
        Assert.Equal(["web"], (await _store.GetSelectionAsync()).SelectedModuleIds);
    }

    [Fact]
    public async Task A_throwing_runner_fails_the_build_without_leaking_the_message()
    {
        var svc = Service();
        await svc.CommitAsync(_admin, ["web"], 0);
        await svc.StartRebuildAsync(_admin, "key-1");

        _executor.Throw();
        await svc.DrainAsync();

        var record = Assert.Single(await svc.GetBuildsAsync());
        Assert.Equal("failed", record.State);
        Assert.DoesNotContain("/secret", record.ReportSummary);
    }

    [Fact]
    public async Task Rebuild_without_a_runner_is_refused_with_a_reason()
    {
        var svc = Service(withExecutor: false);
        await svc.CommitAsync(_admin, ["web"], 0);

        var result = await svc.StartRebuildAsync(_admin, "key-1");

        Assert.False(result.Started);
        Assert.NotNull(result.Reason);
        Assert.Empty(await svc.GetBuildsAsync());
    }

    [Fact]
    public async Task A_build_left_running_by_a_previous_process_is_failed_on_first_use()
    {
        _store.SeedRunning("old");
        var svc = Service();
        await svc.CommitAsync(_admin, ["web"], 0);

        var result = await svc.StartRebuildAsync(_admin, "key-1");

        Assert.True(result.Started);
        Assert.Contains(await svc.GetBuildsAsync(), b => b.State == "failed" && b.ReportSummary!.StartsWith("Abandoned", StringComparison.Ordinal));
        _executor.Finish();
        await svc.DrainAsync();
    }

    [Fact]
    public async Task Rebuild_fails_closed_when_the_audit_entry_cannot_be_appended()
    {
        var svc = Service();
        await svc.CommitAsync(_admin, ["web"], 0);
        _audit.Fail = true;

        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.StartRebuildAsync(_admin, "key-1"));

        Assert.Equal(0, _executor.Runs);
        Assert.Equal("failed", Assert.Single(await svc.GetBuildsAsync()).State);
    }

    // --- modules that need the owner ---

    [Fact]
    public async Task Selecting_a_module_that_needs_approval_files_a_pending_approval_and_does_not_build_it_in()
    {
        var svc = Service(new FakeRequirement("web"));

        var commit = await svc.CommitAsync(_admin, ["web"], 0);

        Assert.True(commit.Committed);
        var request = Assert.Single(_queue.Requests);
        Assert.Equal(ModuleSelectionService.ApprovalKind, request.Kind);
        Assert.Equal(2, request.Tier);
        Assert.Equal("module:web", request.PayloadRef);
        Assert.Equal(_admin, request.RequesterPrincipalId);
        Assert.Contains(commit.Problems, p => p.ModuleId == "web" && p.Message.Contains("Awaiting owner approval", StringComparison.Ordinal));

        var rebuild = await svc.StartRebuildAsync(_admin, "key-1");
        Assert.False(rebuild.Started);
        Assert.Contains("awaiting owner approval", rebuild.Reason);
        Assert.Equal(0, _executor.Runs);
    }

    [Fact]
    public async Task Recommitting_reuses_the_pending_approval_instead_of_filing_another()
    {
        var svc = Service(new FakeRequirement("web"));
        await svc.CommitAsync(_admin, ["web"], 0);

        await svc.CommitAsync(_admin, ["web", "mail"], 1);

        Assert.Single(_queue.Requests);
    }

    [Fact]
    public async Task An_approved_request_lets_the_rebuild_run_and_is_spent_once()
    {
        var svc = Service(new FakeRequirement("web"));
        await svc.CommitAsync(_admin, ["web"], 0);
        var approvalId = (await _store.GetSelectionAsync()).PendingApprovals["web"];
        await _queue.DecideAsync(approvalId, Guid.NewGuid(), true, null);

        var rebuild = await svc.StartRebuildAsync(_admin, "key-1");

        Assert.True(rebuild.Started);
        Assert.Equal(ApprovalStatus.Consumed, (await _queue.GetAsync(approvalId))!.Status);
        Assert.Empty((await _store.GetSelectionAsync()).PendingApprovals);
        _executor.Finish();
        await svc.DrainAsync();
    }

    [Fact]
    public async Task A_denied_request_blocks_the_rebuild_until_the_selection_is_committed_again()
    {
        var svc = Service(new FakeRequirement("web"));
        await svc.CommitAsync(_admin, ["web"], 0);
        await _queue.DecideAsync((await _store.GetSelectionAsync()).PendingApprovals["web"], Guid.NewGuid(), false, "no");

        var rebuild = await svc.StartRebuildAsync(_admin, "key-1");
        Assert.False(rebuild.Started);
        Assert.Contains("denied", rebuild.Reason);

        await svc.CommitAsync(_admin, ["web"], 1);
        Assert.Equal(2, _queue.Requests.Count);
    }

    [Fact]
    public async Task A_module_needing_approval_cannot_be_selected_without_the_queue()
    {
        var svc = Service(new FakeRequirement("web"), withQueue: false);

        var commit = await svc.CommitAsync(_admin, ["web"], 0);

        Assert.False(commit.Committed);
        Assert.Equal(0, (await _store.GetSelectionAsync()).Revision);
    }

    [Fact]
    public async Task A_dependency_that_needs_approval_is_gated_too()
    {
        var svc = Service(new FakeRequirement("core"));

        await svc.CommitAsync(_admin, ["ext"], 0);

        Assert.Equal("module:core", Assert.Single(_queue.Requests).PayloadRef);
    }
}
