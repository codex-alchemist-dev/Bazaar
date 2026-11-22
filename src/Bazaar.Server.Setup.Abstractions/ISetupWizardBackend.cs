namespace Bazaar.Server.Setup;

/// <summary>
/// What a dry run or an apply gave back to the wizard: the exit code, the plan hash when there is a plan, and the text to show.
/// <paramref name="Report"/> is the data the text was rendered from, when the backend has it, for a front end that lays it out itself.
/// </summary>
public sealed record SetupWizardReview(int ExitCode, string? PlanHash, IReadOnlyList<string> Lines, SetupReport? Report = null);

/// <summary>Turns a <see cref="SetupReport"/> into the text lines a front end shows. The CLI and a terminal UI each supply their own.</summary>
public interface ISetupReportRenderer
{
    IReadOnlyList<string> RenderLines(SetupReport report);
}

/// <summary>
/// The dry-run and apply path the wizard drives. The implementation merges the answers (<c>path=value</c> flags) through the same
/// overlay as <c>setup --set</c>, validates, plans and applies, so a wizard run and a flag run with the same answers give the same
/// plan hash. A terminal UI reuses the engine and supplies (or reuses) one of these.
/// </summary>
public interface ISetupWizardBackend
{
    /// <summary>Validates and plans the answers. Writes nothing.</summary>
    SetupWizardReview DryRun(IReadOnlyList<string> setArguments);

    /// <summary>Applies the plan the answers make, when <paramref name="planHash"/> still matches it. The entry path is recorded in the audit row.</summary>
    Task<SetupWizardReview> ApplyAsync(IReadOnlyList<string> setArguments, string planHash, SetupEntryPath entryPath, CancellationToken cancellationToken);
}
