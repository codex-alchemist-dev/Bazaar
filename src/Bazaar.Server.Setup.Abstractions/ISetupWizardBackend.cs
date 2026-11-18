namespace Bazaar.Server.Setup;

/// <summary>What a dry run or an apply gave back to the wizard: the exit code, the plan hash when there is a plan, and the text to show.</summary>
public sealed record SetupWizardReview(int ExitCode, string? PlanHash, IReadOnlyList<string> Lines);

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
