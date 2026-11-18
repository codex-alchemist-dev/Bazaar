using Bazaar.Server.Modules.LineMove;

namespace Bazaar.Server.Tests.Modules.LineMove;

/// <summary>A scripted dry-run executor. Records every call in order; each step can be scripted to fail or to cancel.</summary>
internal sealed class FakeDryRunExecutor : IDryRunExecutor
{
    public List<string> Calls { get; } = [];

    public DryRunStepResult Restore { get; set; } = DryRunStepResult.Success("Snapshot restored into scratch.");

    public DryRunStepResult Migrations { get; set; } = DryRunStepResult.Success("All migrations applied.");

    public Dictionary<string, DryRunStepResult> Checks { get; } = new(StringComparer.Ordinal);

    public Func<CancellationToken, Task>? BeforeCheck { get; set; }

    public Task<DryRunStepResult> RestoreSnapshotAsync(DryRunPlan plan, CancellationToken cancellationToken)
    {
        Calls.Add("restore");
        return Task.FromResult(Restore);
    }

    public Task<DryRunStepResult> RunMigrationsAsync(DryRunPlan plan, CancellationToken cancellationToken)
    {
        Calls.Add("migrations");
        return Task.FromResult(Migrations);
    }

    public async Task<DryRunStepResult> RunConsistencyCheckAsync(GcConsistencyDescriptor descriptor, CancellationToken cancellationToken)
    {
        Calls.Add($"check:{descriptor.GcId}");
        if (BeforeCheck is not null)
            await BeforeCheck(cancellationToken);
        return Checks.TryGetValue(descriptor.GcId, out var result)
            ? result
            : DryRunStepResult.Success($"{descriptor.GcId}: {descriptor.ExpectedRowCount} rows, max sequence {descriptor.ExpectedMaxSequence}.");
    }
}

/// <summary>The dry-run runner: order, stop at the first migration failure, check reporting, validation refusals, cancellation. Fakes only.</summary>
public sealed class DryRunRunnerTests
{
    private static DryRunPlan Plan(
        string snapshotId = "snap-19",
        int sourceLine = 18,
        int targetLine = 19,
        string scratch = "dryrun-19",
        IReadOnlyList<GcConsistencyDescriptor>? checks = null) =>
        new(
            snapshotId,
            sourceLine,
            targetLine,
            scratch,
            ["0001_init", "0002_gc_seq"],
            checks ?? [new GcConsistencyDescriptor("gc-a", 120, 119), new GcConsistencyDescriptor("gc-b", 7, 7)]);

    [Fact]
    public async Task A_happy_path_restores_then_migrates_then_checks_each_gc_in_order_and_passes()
    {
        var executor = new FakeDryRunExecutor();

        var report = await DryRunRunner.RunAsync(Plan(), executor, CancellationToken.None);

        Assert.Equal(["restore", "migrations", "check:gc-a", "check:gc-b"], executor.Calls);
        Assert.Equal(DryRunStatus.Passed, report.Status);
        Assert.Null(report.FirstFailure);
        Assert.Equal("snap-19", report.SnapshotId);
        Assert.Empty(report.Problems);
        Assert.Equal(
            [DryRunStepKind.Restore, DryRunStepKind.Migrations, DryRunStepKind.ConsistencyCheck, DryRunStepKind.ConsistencyCheck],
            report.Steps.Select(step => step.Kind));
        Assert.Equal(["restore", "migrations", "gc-a", "gc-b"], report.Steps.Select(step => step.Name));
        Assert.All(report.Steps, step => Assert.True(step.Succeeded));
    }

    [Fact]
    public async Task A_failed_migration_stops_the_run_before_any_consistency_check()
    {
        var executor = new FakeDryRunExecutor
        {
            Migrations = DryRunStepResult.Failure("Migration 0002_gc_seq failed."),
        };

        var report = await DryRunRunner.RunAsync(Plan(), executor, CancellationToken.None);

        Assert.Equal(["restore", "migrations"], executor.Calls);
        Assert.Equal(DryRunStatus.Failed, report.Status);
        Assert.NotNull(report.FirstFailure);
        Assert.Equal(DryRunStepKind.Migrations, report.FirstFailure.Kind);
        Assert.Equal("Migration 0002_gc_seq failed.", report.FirstFailure.Text);
        Assert.DoesNotContain(report.Steps, step => step.Kind == DryRunStepKind.ConsistencyCheck);
    }

    [Fact]
    public async Task A_failing_consistency_check_fails_the_report_and_the_remaining_checks_still_run()
    {
        var executor = new FakeDryRunExecutor();
        executor.Checks["gc-a"] = DryRunStepResult.Failure("gc-a: 118 rows, expected 120.");

        var report = await DryRunRunner.RunAsync(Plan(), executor, CancellationToken.None);

        Assert.Equal(["restore", "migrations", "check:gc-a", "check:gc-b"], executor.Calls);
        Assert.Equal(DryRunStatus.Failed, report.Status);
        Assert.NotNull(report.FirstFailure);
        Assert.Equal("gc-a", report.FirstFailure.Name);
        Assert.Equal(DryRunStepKind.ConsistencyCheck, report.FirstFailure.Kind);
        Assert.Equal(4, report.Steps.Count);
        Assert.True(report.Steps[3].Succeeded);
    }

    [Fact]
    public async Task A_failed_restore_runs_no_migrations_and_no_checks()
    {
        var executor = new FakeDryRunExecutor
        {
            Restore = DryRunStepResult.Failure("Restore refused: scratch directory is not empty."),
        };

        var report = await DryRunRunner.RunAsync(Plan(), executor, CancellationToken.None);

        Assert.Equal(["restore"], executor.Calls);
        Assert.Equal(DryRunStatus.Failed, report.Status);
        Assert.Equal(DryRunStepKind.Restore, report.FirstFailure?.Kind);
        Assert.Single(report.Steps);
    }

    [Fact]
    public async Task A_plan_with_no_consistency_checks_still_passes_after_migrations()
    {
        var executor = new FakeDryRunExecutor();

        var report = await DryRunRunner.RunAsync(Plan(checks: []), executor, CancellationToken.None);

        Assert.Equal(["restore", "migrations"], executor.Calls);
        Assert.Equal(DryRunStatus.Passed, report.Status);
    }

    [Fact]
    public async Task Each_validation_rule_refuses_the_plan_and_no_step_runs()
    {
        var cases = new (string Name, DryRunPlan Plan, DryRunProblemKind Kind)[]
        {
            ("same line", Plan(sourceLine: 19, targetLine: 19), DryRunProblemKind.SameSourceAndTargetLine),
            ("empty snapshot", Plan(snapshotId: "  "), DryRunProblemKind.EmptySnapshotId),
            ("unsafe scratch", Plan(scratch: "../escape"), DryRunProblemKind.UnsafeScratchDirectoryName),
            ("duplicate gc", Plan(checks: [new GcConsistencyDescriptor("gc-a", 1, 1), new GcConsistencyDescriptor("gc-a", 2, 2)]), DryRunProblemKind.DuplicateGcId),
        };

        foreach (var (name, plan, kind) in cases)
        {
            var executor = new FakeDryRunExecutor();

            var report = await DryRunRunner.RunAsync(plan, executor, CancellationToken.None);

            Assert.True(report.Status == DryRunStatus.Refused, name);
            Assert.Empty(executor.Calls);
            Assert.Empty(report.Steps);
            Assert.Contains(report.Problems, problem => problem.Kind == kind);
        }
    }

    [Fact]
    public async Task A_refused_plan_reports_every_problem_at_once()
    {
        var plan = Plan(snapshotId: "", sourceLine: 19, targetLine: 19, scratch: "a/b", checks: [new GcConsistencyDescriptor("gc-a", 1, 1), new GcConsistencyDescriptor("gc-a", 1, 1)]);

        var report = await DryRunRunner.RunAsync(plan, new FakeDryRunExecutor(), CancellationToken.None);

        Assert.Equal(DryRunStatus.Refused, report.Status);
        Assert.Equal(
            [DryRunProblemKind.SameSourceAndTargetLine, DryRunProblemKind.EmptySnapshotId, DryRunProblemKind.UnsafeScratchDirectoryName, DryRunProblemKind.DuplicateGcId],
            report.Problems.Select(problem => problem.Kind));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("../escape")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData("/absolute")]
    [InlineData("C:scratch")]
    [InlineData("has space")]
    [InlineData(".hidden")]
    [InlineData("trailing.")]
    [InlineData("a..b")]
    public void Unsafe_scratch_names_are_rejected(string name)
    {
        Assert.Contains(Plan(scratch: name).Validate(), problem => problem.Kind == DryRunProblemKind.UnsafeScratchDirectoryName);
    }

    [Theory]
    [InlineData("dryrun-19")]
    [InlineData("scratch_1")]
    [InlineData("move.19.a")]
    public void Plain_scratch_names_are_accepted(string name)
    {
        Assert.Empty(Plan(scratch: name).Validate());
    }

    [Fact]
    public async Task A_run_cancelled_before_it_starts_returns_cancelled_and_calls_nothing()
    {
        var executor = new FakeDryRunExecutor();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var report = await DryRunRunner.RunAsync(Plan(), executor, cts.Token);

        Assert.Equal(DryRunStatus.Cancelled, report.Status);
        Assert.Empty(executor.Calls);
        Assert.Empty(report.Steps);
        Assert.Null(report.FirstFailure);
    }

    [Fact]
    public async Task A_run_cancelled_during_a_check_returns_cancelled_not_passed()
    {
        using var cts = new CancellationTokenSource();
        var executor = new FakeDryRunExecutor
        {
            BeforeCheck = async token =>
            {
                await cts.CancelAsync();
                token.ThrowIfCancellationRequested();
            },
        };

        var report = await DryRunRunner.RunAsync(Plan(), executor, cts.Token);

        Assert.Equal(DryRunStatus.Cancelled, report.Status);
        Assert.Equal(["restore", "migrations", "check:gc-a"], executor.Calls);
        Assert.Equal(2, report.Steps.Count);
        Assert.Null(report.FirstFailure);
    }

    [Fact]
    public async Task A_cancellation_between_steps_is_honoured_even_when_the_executor_ignores_the_token()
    {
        using var cts = new CancellationTokenSource();
        var executor = new FakeDryRunExecutor
        {
            BeforeCheck = async _ => await cts.CancelAsync(),
        };

        var report = await DryRunRunner.RunAsync(Plan(), executor, cts.Token);

        Assert.Equal(DryRunStatus.Cancelled, report.Status);
        Assert.Equal(["restore", "migrations", "check:gc-a"], executor.Calls);
    }

    [Fact]
    public async Task A_null_plan_or_executor_is_a_programming_error()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(() => DryRunRunner.RunAsync(null!, new FakeDryRunExecutor(), CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentNullException>(() => DryRunRunner.RunAsync(Plan(), null!, CancellationToken.None));
    }
}
