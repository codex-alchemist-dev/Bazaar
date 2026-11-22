namespace Bazaar.Server.Setup;

/// <summary>
/// One step of the apply (doc 45 section 1.3). Steps run in ascending <see cref="Order"/>, ties broken by <see cref="Id"/>.
/// </summary>
/// <remarks>
/// Idempotency contract: <see cref="ApplyAsync"/> must be safe to run again. Running it on a state the step has already produced
/// changes nothing and reports success with the same result. A crash mid-apply therefore resumes from the last committed step.
/// A step writes its own transaction and must never write a secret value into any output, error text or result.
/// </remarks>
public interface ISetupStep
{
    /// <summary>Stable id, written to the audit row (for example <c>database.schema</c>).</summary>
    string Id { get; }

    /// <summary>Position in the apply order (doc 45 section 1.3). Lower runs first.</summary>
    int Order { get; }

    /// <summary>
    /// The plan sections (ids from <see cref="SetupPlanSections"/>) this step applies. The applier refuses a plan that asks for a
    /// section no registered step covers, before any step runs. The default is none, so a new step must declare what it does.
    /// </summary>
    IReadOnlyCollection<string> Covers => [];

    /// <summary>
    /// Describes, without changing anything, what <see cref="ApplyAsync"/> would do against the current state: one short line per
    /// change, never a secret and never personal data. The dry-run summary collects these. The default is no lines, for a step whose
    /// changes the plan-leaf dry run already shows.
    /// </summary>
    Task<IReadOnlyList<string>> DescribeAsync(SetupPlan plan, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<string>>([]);

    /// <summary>Applies the step. Idempotent: see the type remarks.</summary>
    Task<SetupStepResult> ApplyAsync(SetupPlan plan, SetupApplyContext context, CancellationToken cancellationToken);

    /// <summary>
    /// Undoes what <see cref="ApplyAsync"/> did, when a later step fails and the apply rolls back. The default does nothing,
    /// for steps with nothing to undo. Rollback runs with a token that is not cancelled, so a rollback always finishes.
    /// </summary>
    Task RollbackAsync(SetupPlan plan, SetupApplyContext context, CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>What a step reports. <paramref name="Message"/> is for the caller and never reaches the audit log.</summary>
public sealed record SetupStepResult(bool Succeeded, string? Message = null)
{
    /// <summary>A step that succeeded.</summary>
    public static SetupStepResult Ok { get; } = new(true);
}

/// <summary>What a step gets besides the plan: the hash, the entry path, the operator, the template and the dry-run changes.</summary>
public sealed record SetupApplyContext(
    string PlanHash,
    SetupEntryPath EntryPath,
    string OperatorIdentity,
    string? TemplateId,
    string? TemplateVersion,
    SetupStateSnapshot CurrentState,
    IReadOnlyList<SetupChange> Changes);
