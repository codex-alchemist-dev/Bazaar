namespace Bazaar.Server.Vault.Writes;

/// <summary>A page to write (doc 18, "Page writes").</summary>
/// <param name="DocId">The document id, written to front matter as a ULID. Stable across updates of the same page.</param>
/// <param name="Path">Relative <c>.md</c> path with '/' separators, under an allowed prefix such as <c>company/memory/</c>.</param>
/// <param name="Title">Front matter title. The Vault forbids <c>#</c>, <c>|</c>, <c>^</c> and <c>:</c> in it.</param>
/// <param name="Type">A front matter document type other than <c>decision</c>, such as <c>memory</c>.</param>
/// <param name="Body">Markdown body.</param>
/// <param name="AgentId">The writing agent; goes to the <c>Bazaar-Agent</c> trailer.</param>
/// <param name="GcId">The GC the write came from, or null; goes to the <c>Bazaar-GC</c> trailer.</param>
public sealed record VaultPageDraft(
    Guid DocId,
    string Path,
    string Title,
    string Type,
    string Body,
    string AgentId,
    string? GcId = null);

/// <summary>What a page write did.</summary>
public enum VaultPageOutcome
{
    /// <summary>A new file was committed.</summary>
    Created,

    /// <summary>An existing page with the same id was rewritten and committed.</summary>
    Updated,

    /// <summary>The page already had exactly this content; nothing was committed.</summary>
    UpToDate,
}

/// <summary>The result of a page write. <see cref="OldCommit"/> and <see cref="NewCommit"/> are equal when nothing was committed.</summary>
public sealed record VaultPageResult(VaultPageOutcome Outcome, string OldCommit, string NewCommit);
