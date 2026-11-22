namespace Bazaar.Server.TalentPool;

/// <summary>How one prompt line changed between two template versions.</summary>
public enum LineChangeKind
{
    /// <summary>The line is in both versions.</summary>
    Same,

    /// <summary>The line is only in the newer version.</summary>
    Added,

    /// <summary>The line is only in the older version.</summary>
    Removed,
}

/// <summary>One line of the prompt diff.</summary>
/// <param name="Kind">Same, added or removed.</param>
/// <param name="Text">The line text.</param>
public sealed record PromptLineChange(LineChangeKind Kind, string Text);

/// <summary>
/// What changed between two versions of one Talent template (docs/design/25, Talent Pool: library with version diffs). A review aid
/// for the Council and the owner: it changes nothing, and each version still needs its own hire vote.
/// </summary>
/// <param name="TemplateId">The template id.</param>
/// <param name="FromVersion">The older version.</param>
/// <param name="ToVersion">The newer version.</param>
/// <param name="NameChange">The old and new name when it changed, else null.</param>
/// <param name="PromptLines">The prompt as a line diff, in order. Empty only when both prompts are empty.</param>
/// <param name="ToolsAdded">Tools the newer version asks for that the older did not. Any is a new Tier 2 grant.</param>
/// <param name="ToolsRemoved">Tools the older version asked for that the newer does not.</param>
/// <param name="ModelsAdded">Models the newer version may run on that the older could not.</param>
/// <param name="ModelsRemoved">Models the older version could run on that the newer cannot.</param>
/// <param name="FilesAdded">Files only the newer version has.</param>
/// <param name="FilesRemoved">Files only the older version has.</param>
public sealed record ManifestDiff(
    string TemplateId,
    int FromVersion,
    int ToVersion,
    (string From, string To)? NameChange,
    IReadOnlyList<PromptLineChange> PromptLines,
    IReadOnlyList<string> ToolsAdded,
    IReadOnlyList<string> ToolsRemoved,
    IReadOnlyList<string> ModelsAdded,
    IReadOnlyList<string> ModelsRemoved,
    IReadOnlyList<string> FilesAdded,
    IReadOnlyList<string> FilesRemoved)
{
    /// <summary>True when the newer version asks for a tool the older did not, so it needs a fresh Tier 2 approval.</summary>
    public bool NeedsNewToolGrant => ToolsAdded.Count > 0;

    /// <summary>True when nothing differs.</summary>
    public bool IsEmpty =>
        NameChange is null && ToolsAdded.Count == 0 && ToolsRemoved.Count == 0 && ModelsAdded.Count == 0 && ModelsRemoved.Count == 0
        && FilesAdded.Count == 0 && FilesRemoved.Count == 0 && PromptLines.All(l => l.Kind == LineChangeKind.Same);
}
