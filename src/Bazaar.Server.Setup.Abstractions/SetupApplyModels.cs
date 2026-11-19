namespace Bazaar.Server.Setup;

/// <summary>The entry path a plan came in through (doc 45 section 1.2). The audit row records it by its wire name.</summary>
public enum SetupEntryPath
{
    Wizard,
    Cli,
    File,
    Api,
    Env,
}

/// <summary>Input to <see cref="SetupApplier.ApplyAsync"/>.</summary>
/// <param name="Plan">The normalized plan from the loader. Validation normalizes it in place, as the loader does.</param>
/// <param name="PlanHash">The hash the operator was given at the dry run. Apply refuses unless it matches the plan.</param>
/// <param name="EntryPath">The door the plan came through.</param>
/// <param name="OperatorIdentity">Who applies. Recorded in the audit rows; must not be blank.</param>
/// <param name="TemplateId">The template used, when there is one.</param>
/// <param name="TemplateVersion">The template version used, when there is one.</param>
/// <param name="CurrentState">The state the dry run compared against. Null means an empty install.</param>
public sealed record SetupApplyRequest(
    SetupPlan Plan,
    string PlanHash,
    SetupEntryPath EntryPath,
    string OperatorIdentity,
    string? TemplateId = null,
    string? TemplateVersion = null,
    SetupStateSnapshot? CurrentState = null);

/// <summary>The outcome of an apply. Exit 0 applied, 1 refused as invalid, 2 held, 3 failed and rolled back (doc 45 section 1.7).</summary>
/// <param name="ExitCode">The exit code the command returns.</param>
/// <param name="PlanHash">The hash of the plan as it was checked.</param>
/// <param name="AppliedStepIds">The steps that completed, in run order. Empty after a rollback.</param>
/// <param name="Issues">Errors, holds and failure notes. They name keys and rules, never values.</param>
public sealed record SetupApplyResult(
    SetupExitCode ExitCode,
    string PlanHash,
    IReadOnlyList<string> AppliedStepIds,
    IReadOnlyList<SetupValidationIssue> Issues);
