namespace Bazaar.Server.Modules.LineMove;

/// <summary>How a dry run ended.</summary>
public enum DryRunStatus
{
    /// <summary>The restore, the migrations and every consistency check passed.</summary>
    Passed,

    /// <summary>A step failed. The report names the first failing step.</summary>
    Failed,

    /// <summary>The plan did not validate. No step ran.</summary>
    Refused,

    /// <summary>The run was cancelled. No success is claimed.</summary>
    Cancelled,
}

/// <summary>The kinds of step a dry run executes.</summary>
public enum DryRunStepKind
{
    /// <summary>Restore of the snapshot into the scratch directory.</summary>
    Restore,

    /// <summary>The migrations of the plan, in order.</summary>
    Migrations,

    /// <summary>One GC consistency check.</summary>
    ConsistencyCheck,
}

/// <summary>One executed step: what it was, its name (the GC id for a check), and its outcome.</summary>
public sealed record DryRunStepRecord(DryRunStepKind Kind, string Name, bool Succeeded, string Text);

/// <summary>
/// The dry-run report the owner sees. <see cref="FirstFailure"/> is the first failed step, or null when none failed.
/// <see cref="Steps"/> holds the steps that ran, in order.
/// </summary>
/// <param name="Status">How the run ended.</param>
/// <param name="SnapshotId">The plan's snapshot id.</param>
/// <param name="Steps">The steps that ran, in order.</param>
/// <param name="FirstFailure">The first failed step, or null.</param>
/// <param name="Problems">The validation problems when <see cref="DryRunStatus.Refused"/>; empty otherwise.</param>
public sealed record DryRunReport(
    DryRunStatus Status,
    string SnapshotId,
    IReadOnlyList<DryRunStepRecord> Steps,
    DryRunStepRecord? FirstFailure,
    IReadOnlyList<DryRunProblem> Problems);

/// <summary>
/// Runs a dry run (doc 42 section 10.3, step 3) over an injected executor: restore, then migrations (stop at the first
/// failure), then every GC consistency check. All checks run even after one fails, so the owner sees every GC.
/// The runner does no I/O; it only sequences the executor and builds the report.
/// </summary>
public static class DryRunRunner
{
    /// <summary>Validates the plan, then runs it. A plan that does not validate returns <see cref="DryRunStatus.Refused"/>.</summary>
    public static async Task<DryRunReport> RunAsync(DryRunPlan plan, IDryRunExecutor executor, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(executor);

        var problems = plan.Validate();
        if (problems.Count > 0)
            return new DryRunReport(DryRunStatus.Refused, plan.SnapshotId, [], FirstFailure: null, problems);

        var steps = new List<DryRunStepRecord>();
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var restore = await executor.RestoreSnapshotAsync(plan, cancellationToken);
            var restoreStep = Record(DryRunStepKind.Restore, "restore", restore);
            steps.Add(restoreStep);
            if (!restore.Succeeded)
                return Finished(DryRunStatus.Failed, plan, steps);

            cancellationToken.ThrowIfCancellationRequested();
            var migrations = await executor.RunMigrationsAsync(plan, cancellationToken);
            steps.Add(Record(DryRunStepKind.Migrations, "migrations", migrations));
            if (!migrations.Succeeded)
                return Finished(DryRunStatus.Failed, plan, steps);

            foreach (var check in plan.ConsistencyChecks)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var result = await executor.RunConsistencyCheckAsync(check, cancellationToken);
                steps.Add(Record(DryRunStepKind.ConsistencyCheck, check.GcId, result));
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Finished(DryRunStatus.Cancelled, plan, steps);
        }

        return Finished(steps.Any(step => !step.Succeeded) ? DryRunStatus.Failed : DryRunStatus.Passed, plan, steps);
    }

    private static DryRunStepRecord Record(DryRunStepKind kind, string name, DryRunStepResult result) =>
        new(kind, name, result.Succeeded, result.Text);

    // A cancelled run never reports Passed, even when every step before the cancellation succeeded.
    private static DryRunReport Finished(DryRunStatus status, DryRunPlan plan, List<DryRunStepRecord> steps) =>
        new(status, plan.SnapshotId, steps, steps.FirstOrDefault(step => !step.Succeeded), []);
}
