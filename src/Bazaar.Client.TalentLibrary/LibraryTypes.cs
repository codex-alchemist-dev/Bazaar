namespace Bazaar.Client.TalentLibrary;

/// <summary>One template in the Talent library: its latest version and where that version stands.</summary>
/// <param name="TemplateId">The stable template id.</param>
/// <param name="Name">The template name.</param>
/// <param name="LatestVersion">The newest version number.</param>
/// <param name="Standing">One of <c>quarantined</c>, <c>vote_pending</c>, <c>released</c>, <c>blocked</c>.</param>
/// <param name="ToolCount">How many tools the latest version asks for.</param>
/// <param name="VersionCount">How many versions exist.</param>
public sealed record LibraryEntry(string TemplateId, string Name, int LatestVersion, string Standing, int ToolCount, int VersionCount);

/// <summary>One stored version of a template.</summary>
public sealed record LibraryVersion(int Version, string ContentSha, string SubmittedBy, DateTimeOffset SubmittedAt, string Standing);

/// <summary>One prompt line of a diff. <c>Kind</c> is <c>same</c>, <c>added</c> or <c>removed</c>.</summary>
public sealed record LibraryLine(string Kind, string Text);

/// <summary>What changed between two versions of one template.</summary>
public sealed record LibraryDiff(
    string TemplateId,
    int FromVersion,
    int ToVersion,
    string? OldName,
    string? NewName,
    LibraryLine[] PromptLines,
    string[] ToolsAdded,
    string[] ToolsRemoved,
    string[] ModelsAdded,
    string[] ModelsRemoved,
    string[] FilesAdded,
    string[] FilesRemoved,
    bool NeedsNewToolGrant);

/// <summary>Where the library reads from. The HTTP module provides the real one.</summary>
public interface ITalentLibrarySource
{
    /// <summary>Lists the latest version of each template.</summary>
    ValueTask<IReadOnlyList<LibraryEntry>> ListAsync(CancellationToken cancellationToken);

    /// <summary>Lists every version of a template, oldest first. Empty when it is unknown.</summary>
    ValueTask<IReadOnlyList<LibraryVersion>> VersionsAsync(string templateId, CancellationToken cancellationToken);

    /// <summary>Diffs two versions. Null when a version does not exist.</summary>
    ValueTask<LibraryDiff?> DiffAsync(string templateId, int olderVersion, int newerVersion, CancellationToken cancellationToken);
}
