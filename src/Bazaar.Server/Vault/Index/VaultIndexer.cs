using Bazaar.Server.Vault.Data;
using Bazaar.Server.Vault.Git;
using Bazaar.Server.Vault.Links;
using Bazaar.Server.Vault.Markdown;

namespace Bazaar.Server.Vault.Index;

/// <summary>
/// Brings the Vault index (doc 18, "Indexing") up to a git commit. Runs a full rebuild when the index has no commit or the
/// indexed commit is missing from the repository; otherwise it applies <c>git diff</c> between the two commits. Every
/// document rewritten is re-resolved against the whole index, so an incremental run ends in the same state as a rebuild.
/// </summary>
/// <remarks>
/// One run at a time per indexer instance. The state row moves only after every write succeeds, so a failed run is simply
/// repeated from the same indexed commit. Each document is its own transaction (<see cref="VaultIndexRepository"/>).
/// Dispose the indexer when the host stops.
/// </remarks>
public sealed class VaultIndexer(
    IGitProvider git,
    VaultIndexRepository index,
    VaultStateRepository state,
    VaultCommitRepository commits,
    VaultAmbiguousLinkPolicy ambiguousLinkPolicy) : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public void Dispose() => _gate.Dispose();

    public async Task<VaultIndexResult> CatchUpAsync(Guid vaultId, string repoPath, string headCommitish, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repoPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(headCommitish);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var head = await git.ResolveCommitAsync(repoPath, headCommitish, cancellationToken).ConfigureAwait(false);
            var indexed = await state.GetIndexedCommitAsync(vaultId, cancellationToken).ConfigureAwait(false);
            if (string.Equals(indexed, head, StringComparison.Ordinal))
            {
                return new VaultIndexResult(head, VaultIndexMode.UpToDate, 0, 0, 0, []);
            }

            if (indexed is not null)
            {
                IReadOnlyList<DiffEntry>? diff = null;
                try
                {
                    diff = await git.DiffAsync(repoPath, indexed, head, cancellationToken).ConfigureAwait(false);
                }
                catch (ArgumentException)
                {
                    // The indexed commit is not in this repository any more: nothing to diff from, so rebuild.
                }

                if (diff is not null)
                {
                    return await IncrementalAsync(vaultId, repoPath, indexed, head, diff, cancellationToken).ConfigureAwait(false);
                }
            }

            return await RebuildAsync(vaultId, repoPath, indexed, head, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<VaultIndexResult> RebuildAsync(Guid vaultId, string repoPath, string? indexed, string head, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var existing = await index.ListEntriesAsync(vaultId, ct).ConfigureAwait(false);
        var existingByPath = existing.ToDictionary(entry => entry.Path, StringComparer.Ordinal);

        var parsed = new List<ParsedVaultDocument>();
        var invalid = new List<string>();
        foreach (var path in await git.ListFilesAsync(repoPath, head, ct).ConfigureAwait(false))
        {
            if (!VaultDocumentParser.IsMarkdown(path))
            {
                continue;
            }

            var docId = existingByPath.TryGetValue(path, out var known) ? known.DocId : Guid.NewGuid();
            var source = await git.ReadTextAsync(repoPath, head, path, ct).ConfigureAwait(false);
            var document = TryParse(vaultId, docId, path, source, head, now, invalid);
            if (document is not null)
            {
                parsed.Add(document);
            }
        }

        var keptIds = parsed.Select(document => document.Doc.DocId).ToHashSet();
        var removed = existing.Where(entry => !keptIds.Contains(entry.DocId)).ToList();
        foreach (var entry in removed)
        {
            await index.DeleteDocumentAsync(entry.DocId, ct).ConfigureAwait(false);
        }

        await WriteDocumentsAsync(parsed, parsed.Select(document => document.Entry).ToList(), ct).ConfigureAwait(false);
        var recorded = await RecordCommitsAsync(vaultId, repoPath, null, head, ct).ConfigureAwait(false);
        await AdvanceAsync(vaultId, indexed, head, ct).ConfigureAwait(false);
        return new VaultIndexResult(head, VaultIndexMode.Rebuilt, parsed.Count, removed.Count, recorded, invalid);
    }

    private async Task<VaultIndexResult> IncrementalAsync(
        Guid vaultId,
        string repoPath,
        string indexed,
        string head,
        IReadOnlyList<DiffEntry> diff,
        CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var existing = await index.ListEntriesAsync(vaultId, ct).ConfigureAwait(false);
        var byPath = existing.ToDictionary(entry => entry.Path, StringComparer.Ordinal);
        var byId = existing.ToDictionary(entry => entry.DocId);

        // Classify the diff. A rename of a Markdown document keeps its id, so links into it stay valid.
        var removedIds = new HashSet<Guid>();
        var renamedIds = new Dictionary<string, Guid>(StringComparer.Ordinal);
        var upsertPaths = new List<string>();
        foreach (var change in diff)
        {
            if (change.Kind == DiffChangeKind.Renamed
                && change.OldPath is not null
                && byPath.TryGetValue(change.OldPath, out var previous))
            {
                if (VaultDocumentParser.IsMarkdown(change.Path))
                {
                    renamedIds[change.Path] = previous.DocId;
                    upsertPaths.Add(change.Path);
                }
                else
                {
                    removedIds.Add(previous.DocId);
                }

                continue;
            }

            if (change.Kind == DiffChangeKind.Deleted)
            {
                if (byPath.TryGetValue(change.Path, out var deleted))
                {
                    removedIds.Add(deleted.DocId);
                }

                continue;
            }

            if (VaultDocumentParser.IsMarkdown(change.Path))
            {
                upsertPaths.Add(change.Path);
            }
        }

        var invalid = new List<string>();
        var changed = new List<ParsedVaultDocument>();
        foreach (var path in upsertPaths.Distinct(StringComparer.Ordinal))
        {
            var docId = renamedIds.TryGetValue(path, out var renamed) ? renamed
                : byPath.TryGetValue(path, out var known) ? known.DocId
                : Guid.NewGuid();
            var source = await git.ReadTextAsync(repoPath, head, path, ct).ConfigureAwait(false);
            var document = TryParse(vaultId, docId, path, source, head, now, invalid);
            if (document is not null)
            {
                changed.Add(document);
            }
            else if (byId.ContainsKey(docId))
            {
                // It was a Vault document and no longer validates (or is no longer front-matter): drop it from the index.
                removedIds.Add(docId);
            }
        }

        foreach (var id in removedIds)
        {
            await index.DeleteDocumentAsync(id, ct).ConfigureAwait(false);
        }

        var changedIds = changed.Select(document => document.Doc.DocId).ToHashSet();

        // Documents whose links could resolve differently now: they link into a changed or removed document, are
        // unresolved, or name a title, alias or path that appeared or disappeared.
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in existing.Where(entry => changedIds.Contains(entry.DocId) || removedIds.Contains(entry.DocId)))
        {
            AddNames(names, entry);
        }

        foreach (var document in changed)
        {
            AddNames(names, document.Entry);
        }

        var linking = await index.FindLinkingDocumentsAsync(vaultId, [.. changedIds, .. removedIds], names, ct).ConfigureAwait(false);
        var affected = new List<ParsedVaultDocument>();
        foreach (var id in linking.Where(id => !changedIds.Contains(id) && !removedIds.Contains(id)))
        {
            if (!byId.TryGetValue(id, out var entry))
            {
                continue;
            }

            var source = await git.ReadTextAsync(repoPath, head, entry.Path, ct).ConfigureAwait(false);
            var stored = await index.GetDocumentAsync(id, ct).ConfigureAwait(false);
            var document = TryParse(vaultId, id, entry.Path, source, stored?.HeadCommit ?? head, stored?.UpdatedAt ?? now, invalid);
            if (document is not null)
            {
                affected.Add(document);
            }
        }

        // The entries the resolver sees: stored entries for everything untouched, parsed entries for what changed.
        var entries = existing
            .Where(entry => !changedIds.Contains(entry.DocId) && !removedIds.Contains(entry.DocId))
            .Concat(changed.Select(document => document.Entry))
            .ToList();

        var toWrite = changed.Concat(affected).ToList();
        await WriteDocumentsAsync(toWrite, entries, ct).ConfigureAwait(false);
        var recorded = await RecordCommitsAsync(vaultId, repoPath, indexed, head, ct).ConfigureAwait(false);
        await AdvanceAsync(vaultId, indexed, head, ct).ConfigureAwait(false);
        return new VaultIndexResult(head, VaultIndexMode.Incremental, toWrite.Count, removedIds.Count, recorded, invalid);
    }

    /// <summary>
    /// Writes documents in two passes: first every document row and its blocks with no links, so that a link can target
    /// any document in the batch; then the links, resolved against <paramref name="entries"/>.
    /// </summary>
    private async Task WriteDocumentsAsync(IReadOnlyList<ParsedVaultDocument> documents, IReadOnlyList<VaultIndexEntry> entries, CancellationToken ct)
    {
        foreach (var document in documents)
        {
            await index.ReplaceDocumentAsync(document.Doc, document.Blocks, [], ct).ConfigureAwait(false);
        }

        foreach (var document in documents)
        {
            await index.ReplaceDocumentAsync(document.Doc, document.Blocks, ResolveLinks(document, entries), ct).ConfigureAwait(false);
        }
    }

    private List<VaultLinkRecord> ResolveLinks(ParsedVaultDocument document, IReadOnlyList<VaultIndexEntry> entries)
    {
        var rows = new List<VaultLinkRecord>(document.Links.Count);
        for (var i = 0; i < document.Links.Count; i++)
        {
            var link = document.Links[i];
            var resolution = VaultLinkResolver.Resolve(link, document.Entry, entries, _ => true, ambiguousLinkPolicy);

            // A same-document link has no target text; the column requires one, so the document's own path stands in.
            var raw = link.Target.Length > 0 ? link.Target : StripMarkdownExtension(document.Entry.Path);
            rows.Add(new VaultLinkRecord(
                document.Doc.DocId,
                document.LinkOrdinals[i],
                i,
                link.Kind == WikiLinkKind.Embed ? "embed" : "link",
                raw,
                resolution.Resolved ? resolution.Target!.DocId : null,
                link.AnchorKind == WikiLinkAnchorKind.None ? null : link.Anchor,
                link.Alias));
        }

        return rows;
    }

    private async Task<int> RecordCommitsAsync(Guid vaultId, string repoPath, string? excludeSha, string head, CancellationToken ct)
    {
        var infos = await git.ListCommitsAsync(repoPath, excludeSha, head, ct).ConfigureAwait(false);
        var records = infos.Select(info => new VaultCommitRecord(
            vaultId,
            info.Sha,
            info.Parents,
            info.AuthorName,
            info.CommittedAt,
            [],
            info.TouchedPaths,
            Trailer(info.Message, "Bazaar-GC"),
            Trailer(info.Message, "Bazaar-Task"))).ToList();
        return await commits.InsertAsync(records, ct).ConfigureAwait(false);
    }

    private async Task AdvanceAsync(Guid vaultId, string? expected, string next, CancellationToken ct)
    {
        if (!await state.TryAdvanceAsync(vaultId, expected, next, ct).ConfigureAwait(false))
        {
            throw new InvalidOperationException("The Vault index moved while it was being updated; the run was not recorded.");
        }
    }

    private static ParsedVaultDocument? TryParse(
        Guid vaultId,
        Guid docId,
        string path,
        string source,
        string commit,
        DateTimeOffset updatedAt,
        List<string> invalid)
    {
        try
        {
            return VaultDocumentParser.TryParse(vaultId, docId, path, source, commit, updatedAt);
        }
        catch (VaultFormatException)
        {
            invalid.Add(path);
            return null;
        }
    }

    private static void AddNames(HashSet<string> names, VaultIndexEntry entry)
    {
        names.Add(StripMarkdownExtension(entry.Path).ToLowerInvariant());
        if (entry.Title is not null)
        {
            names.Add(entry.Title.ToLowerInvariant());
        }

        foreach (var alias in entry.Aliases)
        {
            names.Add(alias.ToLowerInvariant());
        }
    }

    private static string StripMarkdownExtension(string path) =>
        VaultDocumentParser.IsMarkdown(path) ? path[..^3] : path;

    /// <summary>The value of a commit trailer such as <c>Bazaar-GC: &lt;guid&gt;</c>, or null when absent or not a GUID.</summary>
    private static Guid? Trailer(string message, string key)
    {
        foreach (var line in message.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith(key + ":", StringComparison.Ordinal)
                && Guid.TryParse(trimmed[(key.Length + 1)..].Trim(), out var value))
            {
                return value;
            }
        }

        return null;
    }
}
