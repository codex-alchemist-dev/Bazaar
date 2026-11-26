namespace Bazaar.Server.Vault.Writes;

public enum VaultEditOutcome
{
    /// <summary>The branch was merged and a merge commit recorded.</summary>
    Merged,

    /// <summary>The branch was already contained in the main checkout; nothing changed.</summary>
    UpToDate,

    /// <summary>Overlapping edits. Nothing was committed; the branch is kept for the agent to rebase.</summary>
    Conflicted,

    /// <summary>
    /// The branch changes a frozen decision record (one whose status is no longer <c>proposed</c>). Nothing was merged;
    /// <see cref="VaultEditResult.ConflictPaths"/> lists the refused paths.
    /// </summary>
    Refused,
}

/// <summary>An agent's request to land its edit branch.</summary>
/// <param name="DocId">The document the edit is about (the per-document queue key).</param>
/// <param name="BaseCommit">The commit the agent started from (<c>Bazaar-Edit-Base</c> trailer).</param>
/// <param name="Branch">The agent's <c>agent/&lt;task&gt;</c> branch.</param>
/// <param name="AgentId">The agent's identity (<c>Bazaar-Agent</c> trailer).</param>
/// <param name="GcId">The GC the edit belongs to, or null for memory writes (<c>Bazaar-GC</c> trailer).</param>
/// <param name="TaskId">The task, or null (<c>Bazaar-Task</c> trailer).</param>
/// <param name="ExtraTrailers">Further trailers for the merge commit, in order (for example offline-write ids); null for none.</param>
public sealed record VaultEditRequest(
    Guid DocId,
    string BaseCommit,
    string Branch,
    string AgentId,
    string? GcId,
    string? TaskId,
    IReadOnlyList<KeyValuePair<string, string>>? ExtraTrailers = null);

/// <summary>A new decision record. The writer assigns the number and the file path.</summary>
/// <param name="DocId">The document id.</param>
/// <param name="Slug">The file slug.</param>
/// <param name="Title">The title.</param>
/// <param name="Body">The body.</param>
/// <param name="AgentId">The authoring agent.</param>
/// <param name="Supersedes">Decision references (<c>0001-slug</c>) this record replaces; written to the <c>supersedes</c> front matter list.</param>
/// <param name="DecidedIn">The GC message reference that decided the record; written to the <c>decidedIn</c> front matter key.</param>
public sealed record DecisionDraft(
    Guid DocId,
    string Slug,
    string Title,
    string Body,
    string AgentId,
    IReadOnlyList<string>? Supersedes = null,
    string? DecidedIn = null);

/// <summary>
/// What a write did. <see cref="OldCommit"/> and <see cref="NewCommit"/> feed the indexer. <see cref="ConflictPaths"/> is
/// filled only when conflicted. <see cref="DecisionNumber"/> is set for decision records.
/// </summary>
public sealed record VaultEditResult(
    VaultEditOutcome Outcome,
    string OldCommit,
    string NewCommit,
    IReadOnlyList<string> ConflictPaths,
    int? DecisionNumber);
