namespace Bazaar.Server.Setup;

/// <summary>The result of merging the doors into one plan document.</summary>
/// <param name="MergedJson">The merged document for <see cref="SetupPlanLoader.Load"/>. Null when a door has an error, so nothing downstream uses a partial merge.</param>
/// <param name="Doors">The doors that contributed. Kept beside the plan, not inside it, so the plan hash is the same whichever door was used.</param>
/// <param name="Issues">Every error found while merging. Each names a key path and a rule, never a value.</param>
public sealed record SetupOverlayResult(string? MergedJson, SetupDoors Doors, IReadOnlyList<SetupValidationIssue> Issues)
{
    /// <summary>True when a door could not be merged. The loader is then not run.</summary>
    public bool HasErrors => Issues.Any(issue => issue.Severity == SetupIssueSeverity.Error);

    /// <summary>The entry path the apply records (see <see cref="SetupDoorRules.EntryPathFor"/>).</summary>
    public SetupEntryPath EntryPath => SetupDoorRules.EntryPathFor(Doors);
}
