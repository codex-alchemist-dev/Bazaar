using Bazaar.Server.Modules.LineMove;

namespace Bazaar.Server.Tests.Modules.LineMove;

/// <summary>The line-move runner: order, stop at first failure, audit, rollback window, cancellation. Fakes only; no database or process.</summary>
public sealed class LineMoveRunnerTests
{
    private static LineMoveRunner Runner(FakeSteps steps, InMemoryAuditSink audit) =>
        new(steps.Build(), audit, LineMoveTestData.Clock());

    [Fact]
    public async Task A_happy_path_runs_every_step_in_order_and_completes()
    {
        var steps = new FakeSteps();
        var audit = new InMemoryAuditSink();

        var outcome = await Runner(steps, audit).RunAsync(LineMoveTestData.Request(), CancellationToken.None);

        Assert.Equal(
            [LineMoveStep.Snapshot, LineMoveStep.DryRun, LineMoveStep.Switch, LineMoveStep.Verify, LineMoveStep.Complete],
            steps.Ran);
        Assert.Equal(LineMoveStatus.Completed, outcome.Status);
        Assert.Equal(LineMoveStep.Complete, outcome.LastStep);
        Assert.Equal("snap-1", outcome.SnapshotId);
        Assert.False(outcome.RollbackRequired);
    }

    [Fact]
    public async Task A_completed_move_emits_the_new_line_as_newest_and_the_old_line_as_stable()
    {
        var steps = new FakeSteps();

        var outcome = await Runner(steps, new InMemoryAuditSink()).RunAsync(LineMoveTestData.Request(), CancellationToken.None);

        Assert.Equal(
            [new LineRoleAssignment(19, LineRole.Newest), new LineRoleAssignment(18, LineRole.Stable)],
            outcome.RoleAssignments);
    }

    [Fact]
    public async Task A_run_that_fails_before_verify_emits_no_role_changes()
    {
        var steps = new FakeSteps().Fail(LineMoveStep.Verify, "Kodo red on line 19.");

        var outcome = await Runner(steps, new InMemoryAuditSink()).RunAsync(LineMoveTestData.Request(), CancellationToken.None);

        Assert.Empty(outcome.RoleAssignments);
    }

    [Fact]
    public async Task A_failed_step_stops_the_run_at_that_step()
    {
        var steps = new FakeSteps().Fail(LineMoveStep.DryRun, "Migration 0042 failed on the scratch copy.");

        var outcome = await Runner(steps, new InMemoryAuditSink()).RunAsync(LineMoveTestData.Request(), CancellationToken.None);

        Assert.Equal([LineMoveStep.Snapshot, LineMoveStep.DryRun], steps.Ran);
        Assert.Equal(LineMoveStatus.Failed, outcome.Status);
        Assert.Equal(LineMoveStep.DryRun, outcome.LastStep);
        Assert.False(outcome.RollbackRequired);
        Assert.DoesNotContain(LineMoveStep.Switch, steps.Ran);
        Assert.DoesNotContain(LineMoveStep.RolledBack, steps.Ran);
    }

    [Fact]
    public async Task A_snapshot_that_reports_no_snapshot_id_fails_the_run()
    {
        var steps = new FakeSteps().Script(LineMoveStep.Snapshot, (_, _) => Task.FromResult(StepResult.Success("Snapshot taken, no id.")));

        var outcome = await Runner(steps, new InMemoryAuditSink()).RunAsync(LineMoveTestData.Request(), CancellationToken.None);

        Assert.Equal(LineMoveStatus.Failed, outcome.Status);
        Assert.Equal(LineMoveStep.Snapshot, outcome.LastStep);
        Assert.Equal([LineMoveStep.Snapshot], steps.Ran);
    }

    [Fact]
    public async Task Each_step_writes_one_audit_event_carrying_the_snapshot_id_and_the_approving_role()
    {
        var steps = new FakeSteps();
        var audit = new InMemoryAuditSink();

        await Runner(steps, audit).RunAsync(LineMoveTestData.Request(), CancellationToken.None);

        Assert.Equal(
            [LineMoveStep.Preflight, LineMoveStep.Snapshot, LineMoveStep.DryRun, LineMoveStep.Switch, LineMoveStep.Verify, LineMoveStep.Complete],
            audit.Events.Select(e => e.Step));
        Assert.All(audit.Events, e => Assert.Equal(LineMoveAuditKind.Succeeded, e.Kind));
        Assert.All(audit.Events, e => Assert.Equal("owner", e.ApprovingRole));
        Assert.All(audit.Events, e => Assert.Equal("move-1", e.MoveId));
        Assert.All(audit.Events, e => Assert.Equal(LineMoveTestData.Now, e.At));
    }

    [Fact]
    public async Task Audit_events_name_the_snapshot_from_the_snapshot_step_onward()
    {
        var audit = new InMemoryAuditSink();

        await Runner(new FakeSteps(), audit).RunAsync(LineMoveTestData.Request(), CancellationToken.None);

        // The snapshot event itself names the snapshot it produced; nothing before it can.
        Assert.Null(audit.Events.Single(e => e.Step == LineMoveStep.Preflight).SnapshotId);
        Assert.Equal("snap-1", audit.Events.Single(e => e.Step == LineMoveStep.Snapshot).SnapshotId);
        Assert.All(audit.Events.Where(e => e.Step is not (LineMoveStep.Preflight or LineMoveStep.Snapshot)), e => Assert.Equal("snap-1", e.SnapshotId));
    }

    [Fact]
    public async Task Executors_see_the_snapshot_id_after_the_snapshot_step()
    {
        var steps = new FakeSteps();

        await Runner(steps, new InMemoryAuditSink()).RunAsync(LineMoveTestData.Request(), CancellationToken.None);

        Assert.Null(steps.Executors[LineMoveStep.Snapshot].Contexts.Single().SnapshotId);
        Assert.Equal("snap-1", steps.Executors[LineMoveStep.DryRun].Contexts.Single().SnapshotId);
        Assert.Equal("snap-1", steps.Executors[LineMoveStep.Verify].Contexts.Single().SnapshotId);
    }

    [Fact]
    public async Task A_failed_step_is_audited_with_its_reason()
    {
        var steps = new FakeSteps().Fail(LineMoveStep.DryRun, "Row count mismatch on gc 7.");
        var audit = new InMemoryAuditSink();

        await Runner(steps, audit).RunAsync(LineMoveTestData.Request(), CancellationToken.None);

        var failed = audit.Events.Single(e => e.Kind == LineMoveAuditKind.Failed);
        Assert.Equal(LineMoveStep.DryRun, failed.Step);
        Assert.Equal("Row count mismatch on gc 7.", failed.Detail);
        Assert.Equal("snap-1", failed.SnapshotId);
        Assert.DoesNotContain(audit.Events, e => e.Step == LineMoveStep.Switch);
    }

    [Fact]
    public async Task A_verify_failure_inside_the_rollback_window_rolls_back()
    {
        var steps = new FakeSteps().Fail(LineMoveStep.Verify, "Health contract red on line 19.");
        var audit = new InMemoryAuditSink();

        var outcome = await Runner(steps, audit).RunAsync(LineMoveTestData.Request(windowOpen: true), CancellationToken.None);

        Assert.Equal(
            [LineMoveStep.Snapshot, LineMoveStep.DryRun, LineMoveStep.Switch, LineMoveStep.Verify, LineMoveStep.RolledBack],
            steps.Ran);
        Assert.Equal(LineMoveStatus.RolledBack, outcome.Status);
        Assert.False(outcome.RollbackRequired);
        Assert.Empty(outcome.RoleAssignments);
        Assert.DoesNotContain(LineMoveStep.Complete, steps.Ran);
        Assert.Contains(audit.Events, e => e.Step == LineMoveStep.RolledBack && e.Kind == LineMoveAuditKind.Succeeded && e.SnapshotId == "snap-1");
    }

    [Fact]
    public async Task A_verify_failure_outside_the_rollback_window_stops_with_manual_rollback_required()
    {
        var steps = new FakeSteps().Fail(LineMoveStep.Verify, "GC consistency check failed.");
        var audit = new InMemoryAuditSink();

        var outcome = await Runner(steps, audit).RunAsync(LineMoveTestData.Request(windowOpen: false), CancellationToken.None);

        Assert.Equal(LineMoveStatus.ManualRollbackRequired, outcome.Status);
        Assert.True(outcome.RollbackRequired);
        Assert.DoesNotContain(LineMoveStep.RolledBack, steps.Ran);
        Assert.Contains(audit.Events, e => e.Kind == LineMoveAuditKind.ManualRollbackRequired && e.SnapshotId == "snap-1");
    }

    [Fact]
    public async Task A_rollback_executor_that_fails_leaves_the_move_for_a_manual_rollback()
    {
        var steps = new FakeSteps()
            .Fail(LineMoveStep.Verify, "Health red.")
            .Fail(LineMoveStep.RolledBack, "Snapshot restore failed.");
        var audit = new InMemoryAuditSink();

        var outcome = await Runner(steps, audit).RunAsync(LineMoveTestData.Request(windowOpen: true), CancellationToken.None);

        Assert.Equal(LineMoveStatus.ManualRollbackRequired, outcome.Status);
        Assert.Equal(LineMoveStep.RolledBack, outcome.LastStep);
        Assert.True(outcome.RollbackRequired);
        Assert.Contains(audit.Events, e => e.Step == LineMoveStep.RolledBack && e.Kind == LineMoveAuditKind.ManualRollbackRequired && e.Detail == "Snapshot restore failed.");
    }

    [Fact]
    public async Task A_switch_failure_is_a_manual_rollback_decision_and_never_rolls_back_automatically()
    {
        var steps = new FakeSteps().Fail(LineMoveStep.Switch, "Old line did not stop cleanly.");

        var outcome = await Runner(steps, new InMemoryAuditSink()).RunAsync(LineMoveTestData.Request(windowOpen: true), CancellationToken.None);

        Assert.Equal(LineMoveStatus.ManualRollbackRequired, outcome.Status);
        Assert.Equal(LineMoveStep.Switch, outcome.LastStep);
        Assert.True(outcome.RollbackRequired);
        Assert.DoesNotContain(LineMoveStep.Verify, steps.Ran);
        Assert.DoesNotContain(LineMoveStep.RolledBack, steps.Ran);
    }

    [Fact]
    public async Task A_run_with_a_failed_preflight_runs_no_executor_and_audits_the_failed_rule()
    {
        var preflight = LineMoveTestData.HealthyPreflight() with { KodoGreenOnCurrentLine = false };
        var steps = new FakeSteps();
        var audit = new InMemoryAuditSink();

        var outcome = await Runner(steps, audit).RunAsync(LineMoveTestData.Request(preflight: preflight), CancellationToken.None);

        Assert.Empty(steps.Ran);
        Assert.Equal(LineMoveStatus.Failed, outcome.Status);
        Assert.Equal(LineMoveStep.Preflight, outcome.LastStep);
        Assert.Null(outcome.SnapshotId);
        var single = Assert.Single(audit.Events);
        Assert.Equal(LineMoveAuditKind.Failed, single.Kind);
        Assert.Contains("Kodo is not green", single.Detail);
    }

    [Fact]
    public async Task A_cancellation_before_the_run_records_cancelled_and_claims_no_success()
    {
        var steps = new FakeSteps();
        var audit = new InMemoryAuditSink();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var outcome = await Runner(steps, audit).RunAsync(LineMoveTestData.Request(), cts.Token);

        Assert.Empty(steps.Ran);
        Assert.Equal(LineMoveStatus.Cancelled, outcome.Status);
        Assert.False(outcome.RollbackRequired);
        Assert.Empty(outcome.RoleAssignments);
        var single = Assert.Single(audit.Events);
        Assert.Equal(LineMoveAuditKind.Cancelled, single.Kind);
        Assert.Equal(LineMoveStep.Preflight, single.Step);
    }

    [Fact]
    public async Task A_cancellation_during_a_step_stops_without_partial_success()
    {
        using var cts = new CancellationTokenSource();
        var steps = new FakeSteps().Script(LineMoveStep.DryRun, async (_, ct) =>
        {
            await cts.CancelAsync();
            ct.ThrowIfCancellationRequested();
            return StepResult.Success("unreachable");
        });
        var audit = new InMemoryAuditSink();

        var outcome = await Runner(steps, audit).RunAsync(LineMoveTestData.Request(), cts.Token);

        Assert.Equal(LineMoveStatus.Cancelled, outcome.Status);
        Assert.Equal(LineMoveStep.DryRun, outcome.LastStep);
        Assert.False(outcome.RollbackRequired);
        Assert.Empty(outcome.RoleAssignments);
        Assert.DoesNotContain(LineMoveStep.Switch, steps.Ran);
        Assert.DoesNotContain(audit.Events, e => e.Step == LineMoveStep.DryRun && e.Kind == LineMoveAuditKind.Succeeded);
        Assert.Equal(LineMoveAuditKind.Cancelled, audit.Events[^1].Kind);
    }

    [Fact]
    public async Task A_cancellation_after_the_switch_flags_that_a_rollback_decision_is_needed()
    {
        using var cts = new CancellationTokenSource();
        var steps = new FakeSteps().Script(LineMoveStep.Verify, async (_, ct) =>
        {
            await cts.CancelAsync();
            ct.ThrowIfCancellationRequested();
            return StepResult.Success("unreachable");
        });

        var outcome = await Runner(steps, new InMemoryAuditSink()).RunAsync(LineMoveTestData.Request(windowOpen: true), cts.Token);

        Assert.Equal(LineMoveStatus.Cancelled, outcome.Status);
        Assert.Equal(LineMoveStep.Verify, outcome.LastStep);
        Assert.True(outcome.RollbackRequired);
        Assert.DoesNotContain(LineMoveStep.RolledBack, steps.Ran);
        Assert.Empty(outcome.RoleAssignments);
    }

    [Fact]
    public async Task A_rollback_is_not_cancelled_by_the_caller_token()
    {
        using var cts = new CancellationTokenSource();
        var steps = new FakeSteps()
            .Script(LineMoveStep.Verify, (_, _) =>
            {
                cts.Cancel();
                return Task.FromResult(StepResult.Failure("Health red."));
            })
            .Script(LineMoveStep.RolledBack, (_, ct) => Task.FromResult(
                ct.IsCancellationRequested ? StepResult.Failure("cancelled") : StepResult.Success("restored")));

        var outcome = await Runner(steps, new InMemoryAuditSink()).RunAsync(LineMoveTestData.Request(windowOpen: true), cts.Token);

        Assert.Equal(LineMoveStatus.RolledBack, outcome.Status);
    }

    [Fact]
    public async Task A_downgrade_is_rejected_before_any_step_runs()
    {
        var steps = new FakeSteps();
        var request = LineMoveTestData.Request() with { FromLine = 19, ToLine = 18 };

        await Assert.ThrowsAsync<ArgumentException>(() => Runner(steps, new InMemoryAuditSink()).RunAsync(request, CancellationToken.None));
        Assert.Empty(steps.Ran);
    }

    [Fact]
    public async Task A_move_to_the_same_line_is_rejected()
    {
        var request = LineMoveTestData.Request() with { FromLine = 19, ToLine = 19 };

        await Assert.ThrowsAsync<ArgumentException>(() => Runner(new FakeSteps(), new InMemoryAuditSink()).RunAsync(request, CancellationToken.None));
    }

    [Fact]
    public void A_runner_refuses_to_start_without_an_executor_for_every_step()
    {
        var steps = new FakeSteps().Build().Where(e => e.ForStep != LineMoveStep.Switch).ToList();

        var ex = Assert.Throws<ArgumentException>(() => new LineMoveRunner(steps, new InMemoryAuditSink(), LineMoveTestData.Clock()));
        Assert.Contains("Switch", ex.Message);
    }

    [Fact]
    public void A_runner_refuses_two_executors_for_one_step()
    {
        var ran = new List<LineMoveStep>();
        var executors = new FakeSteps().Build().Append(new FakeExecutor(LineMoveStep.Snapshot, ran)).ToList();

        Assert.Throws<ArgumentException>(() => new LineMoveRunner(executors, new InMemoryAuditSink(), LineMoveTestData.Clock()));
    }

    [Fact]
    public void Preflight_is_not_an_executor_step()
    {
        var ran = new List<LineMoveStep>();
        var executors = new FakeSteps().Build().Append(new FakeExecutor(LineMoveStep.Preflight, ran)).ToList();

        Assert.Throws<ArgumentException>(() => new LineMoveRunner(executors, new InMemoryAuditSink(), LineMoveTestData.Clock()));
    }
}
