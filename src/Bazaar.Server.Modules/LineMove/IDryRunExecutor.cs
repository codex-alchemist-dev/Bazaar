namespace Bazaar.Server.Modules.LineMove;

/// <summary>
/// What a dry-run executor reports. A success carries evidence; a failure carries the reason. Cancellation is not a
/// result: executors throw <see cref="OperationCanceledException"/>.
/// </summary>
public sealed record DryRunStepResult
{
    private DryRunStepResult(bool succeeded, string text)
    {
        Succeeded = succeeded;
        Text = text;
    }

    /// <summary>True when the step passed.</summary>
    public bool Succeeded { get; }

    /// <summary>Evidence on success, the failure reason otherwise.</summary>
    public string Text { get; }

    /// <summary>A passed step with its evidence.</summary>
    public static DryRunStepResult Success(string evidence)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(evidence);
        return new DryRunStepResult(true, evidence);
    }

    /// <summary>A failed step with its reason.</summary>
    public static DryRunStepResult Failure(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        return new DryRunStepResult(false, reason);
    }
}

/// <summary>
/// The side effects of a dry run, abstracted so the runner stays free of I/O. The production implementation restores
/// into the scratch directory, runs the migrations and reads the GC tables; tests fake all three.
/// Implementations must touch only the scratch directory named in the plan.
/// </summary>
public interface IDryRunExecutor
{
    /// <summary>Restores the plan's snapshot into the scratch data directory.</summary>
    Task<DryRunStepResult> RestoreSnapshotAsync(DryRunPlan plan, CancellationToken cancellationToken);

    /// <summary>
    /// Runs <see cref="DryRunPlan.MigrationNames"/> against the scratch directory, in plan order, and stops at the first
    /// migration that fails. The failure reason names that migration.
    /// </summary>
    Task<DryRunStepResult> RunMigrationsAsync(DryRunPlan plan, CancellationToken cancellationToken);

    /// <summary>Compares one GC's row count and maximum sequence in the scratch directory with the descriptor.</summary>
    Task<DryRunStepResult> RunConsistencyCheckAsync(GcConsistencyDescriptor descriptor, CancellationToken cancellationToken);
}
