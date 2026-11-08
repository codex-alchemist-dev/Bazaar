using System.Text;
using Bazaar.Server.Vault.Links;
using Bazaar.Server.Vault.Markdown;
using Bazaar.Server.Vault.Search;

namespace Bazaar.Server.Vault.Writes;

/// <summary>
/// Move and rename with link rewrite (doc 18, work packages 8 and 12). The move runs in the main checkout under the vault
/// lane, so it lands as one commit: the file moves, and every wiki-link that resolves to it by path points at the new path.
/// </summary>
/// <remarks>
/// <para>
/// <b>Principal checks.</b> The caller must be able to read the moved document. Links are resolved only through documents
/// the caller can read, so a private document of another agent never resolves and never counts as ambiguity. A move that
/// would rewrite a document the caller cannot read is refused whole, with the blocked paths listed, so no partial rename
/// reaches the Vault.
/// </para>
/// <para>
/// Only links that match by path are rewritten. A link that matched by title or alias still resolves after the move, so
/// it keeps its text. Ambiguous links are left alone; a warning would hide a real choice from the author.
/// </para>
/// </remarks>
public sealed partial class VaultWriter
{
    private const string _markdownExtension = ".md";

    /// <summary>
    /// Moves a document to a new path and rewrites the links that pointed at the old path. Frozen decision records
    /// (status other than <c>proposed</c>) are refused, because a move is a rename.
    /// </summary>
    public async Task<VaultEditResult> MoveAsync(VaultMoveRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Caller);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.AgentId);
        ValidateTrailerValue(request.AgentId, nameof(request.AgentId));
        ValidateOptionalTrailerValue(request.GcId, nameof(request.GcId));
        ValidateOptionalTrailerValue(request.TaskId, nameof(request.TaskId));
        ValidateVaultPath(request.FromPath, nameof(request.FromPath));
        ValidateVaultPath(request.ToPath, nameof(request.ToPath));
        if (string.Equals(request.FromPath, request.ToPath, StringComparison.Ordinal))
        {
            throw new ArgumentException("The new path must differ from the old one.", nameof(request));
        }

        using var documentLease = await AcquireAsync(_documentQueues.GetOrAdd(request.DocId, _ => new SemaphoreSlim(1, 1)), ct);
        using var laneLease = await AcquireAsync(_vaultLane, ct);

        var oldCommit = await _git.HeadAsync(_repoPath, ct);
        var fromFull = ResolveInside(_repoPath, request.FromPath);
        var toFull = ResolveInside(_repoPath, request.ToPath);
        if (!File.Exists(fromFull))
        {
            throw new FileNotFoundException("No document at the old path.", request.FromPath);
        }

        if (File.Exists(toFull))
        {
            throw new ArgumentException("A document already exists at the new path.", nameof(request));
        }

        var fromText = await File.ReadAllTextAsync(fromFull, ct);
        if (IsFrozenDecision(request.FromPath, fromText))
        {
            return new VaultEditResult(VaultEditOutcome.Refused, oldCommit, oldCommit, [request.FromPath], null);
        }

        if (!VaultAccess.CanRead(OwnerOf(fromText), request.Caller))
        {
            return new VaultEditResult(VaultEditOutcome.Refused, oldCommit, oldCommit, [request.FromPath], null);
        }

        var (index, owners) = await BuildIndexAsync(ct);
        var (rewrites, blocked) = await PlanLinkRewritesAsync(index, owners, request, ct);
        if (blocked)
        {
            // Only the moved path is named: listing a private document that links to it would reveal that document.
            return new VaultEditResult(VaultEditOutcome.Refused, oldCommit, oldCommit, [request.FromPath], null);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(toFull)!);
        File.Move(fromFull, toFull);
        foreach (var (path, text) in rewrites)
        {
            // The moved document's own links are rewritten at its new location.
            var target = string.Equals(path, request.FromPath, StringComparison.Ordinal) ? toFull : ResolveInside(_repoPath, path);
            await File.WriteAllTextAsync(target, text, new UTF8Encoding(false), ct);
        }

        var message = new StringBuilder($"Move {request.FromPath} to {request.ToPath}");
        message.Append("\n\n");
        AppendTrailers(message, request.AgentId, request.GcId, request.TaskId, oldCommit);
        var newCommit = await _git.CommitAsync(_repoPath, message.ToString(), _writer, ct);

        return new VaultEditResult(VaultEditOutcome.Merged, oldCommit, newCommit, [], null);
    }

    /// <summary>
    /// Builds the index entries for every Markdown file in the main checkout, ordinal-sorted by path, and the owner of each
    /// document (null for shared documents).
    /// </summary>
    private async Task<(List<VaultIndexEntry> Index, Dictionary<string, string?> Owners)> BuildIndexAsync(CancellationToken ct)
    {
        var entries = new List<VaultIndexEntry>();
        var owners = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var full in Directory.EnumerateFiles(_repoPath, "*" + _markdownExtension, SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(_repoPath, full).Replace('\\', '/');
            if (relative.StartsWith(".git/", StringComparison.Ordinal))
            {
                continue;
            }

            var document = VaultMarkdown.Parse(await File.ReadAllTextAsync(full, ct));
            var front = document.FrontMatter;
            var docId = front is not null && Guid.TryParse(front.Id, out var parsed) ? parsed : Guid.Empty;
            entries.Add(new VaultIndexEntry(
                docId,
                relative,
                front?.Title,
                front?.Aliases ?? [],
                front?.Project,
                [],
                []));
            owners[relative] = front?.Owner;
        }

        return (entries.OrderBy(entry => entry.Path, StringComparer.Ordinal).ToList(), owners);
    }

    /// <summary>
    /// Works out the rewritten text of every document that links to the moved one by path. Returns the new text keyed by
    /// the path it has before the move. <c>blocked</c> is true when the move would rewrite a document the caller cannot
    /// read; the caller then refuses the whole move.
    /// </summary>
    private async Task<(IReadOnlyDictionary<string, string> Rewrites, bool Blocked)> PlanLinkRewritesAsync(
        List<VaultIndexEntry> index,
        Dictionary<string, string?> owners,
        VaultMoveRequest request,
        CancellationToken ct)
    {
        var rewrites = new Dictionary<string, string>(StringComparer.Ordinal);
        var blocked = false;
        var oldTarget = StripExtension(request.FromPath);
        var newTarget = StripExtension(request.ToPath);

        // Entries the caller cannot read are removed before resolution, so they never match and never cause ambiguity.
        bool CanSee(VaultIndexEntry entry) => VaultAccess.CanRead(owners[entry.Path], request.Caller);

        foreach (var entry in index)
        {
            var text = await File.ReadAllTextAsync(ResolveInside(_repoPath, entry.Path), ct);
            var document = VaultMarkdown.Parse(text);
            var edits = new List<SourceEdit>();
            foreach (var link in document.Links)
            {
                if (!string.Equals(StripExtension(link.Target), oldTarget, StringComparison.Ordinal))
                {
                    continue;
                }

                // The resolver decides what the link means; only an unambiguous path match is rewritten.
                var resolution = VaultLinkResolver.Resolve(link, entry, index, CanSee, VaultAmbiguousLinkPolicy.Refuse);
                if (!resolution.Resolved || resolution.Warning is not null
                    || !string.Equals(resolution.Target?.Path, request.FromPath, StringComparison.Ordinal))
                {
                    continue;
                }

                var linkText = text.Substring(link.Start, link.Length);
                var prefixLength = link.Kind == WikiLinkKind.Embed ? 3 : 2;
                var targetAt = linkText.IndexOf(link.Target, prefixLength, StringComparison.Ordinal);
                if (targetAt < 0)
                {
                    continue;
                }

                var replacementTarget = link.Target.EndsWith(_markdownExtension, StringComparison.Ordinal) ? request.ToPath : newTarget;
                var replaced = linkText.Remove(targetAt, link.Target.Length).Insert(targetAt, replacementTarget);
                edits.Add(new SourceEdit(link.Start, link.Length, replaced));
            }

            if (edits.Count == 0)
            {
                continue;
            }

            // A link inside a document the caller cannot read would have to change, which the caller may not do.
            if (!CanSee(entry))
            {
                blocked = true;
                break;
            }

            rewrites[entry.Path] = document.Render(edits);
        }

        return (rewrites, blocked);
    }

    private static bool IsFrozenDecision(string path, string text) =>
        path.StartsWith("decisions/", StringComparison.Ordinal)
        && DecisionImmutability.StatusOf(text) is { } status
        && status != "proposed";

    /// <summary>The owner named in front matter, in the form <see cref="VaultAccess"/> compares; null for shared documents.</summary>
    private static string? OwnerOf(string text)
    {
        try
        {
            return VaultMarkdown.Parse(text).FrontMatter?.Owner;
        }
        catch (VaultFormatException)
        {
            // A document with broken front matter is treated as private to nobody it can name: no owner means shared,
            // so the caller must not be able to move it past the check. Fail closed instead.
            throw new InvalidOperationException("Cannot check access on a document with invalid front matter.");
        }
    }

    private static void ValidateVaultPath(string path, string paramName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!path.EndsWith(_markdownExtension, StringComparison.Ordinal)
            || path.StartsWith('/') || path.StartsWith('\\') || Path.IsPathRooted(path)
            || path.Split('/', '\\').Any(part => part is ".." or ".git" || part.Length == 0))
        {
            throw new ArgumentException("A Vault path is a relative .md path with no '..' or .git segment.", paramName);
        }
    }

    /// <summary>Resolves a relative Vault path inside the repository, refusing anything that leaves it.</summary>
    private static string ResolveInside(string repoPath, string relative)
    {
        var full = Path.GetFullPath(Path.Combine(repoPath, relative));
        var root = Path.GetFullPath(repoPath).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(root, StringComparison.Ordinal))
        {
            throw new ArgumentException("The path leaves the Vault repository.", nameof(relative));
        }

        return full;
    }

    private static string StripExtension(string path) =>
        path.EndsWith(_markdownExtension, StringComparison.Ordinal) ? path[..^_markdownExtension.Length] : path;
}

/// <summary>
/// A request to move or rename a document. Paths are relative to the Vault root, using '/'. <see cref="Caller"/> is the
/// principal making the change; the move checks it against every document it would touch.
/// </summary>
public sealed record VaultMoveRequest(
    Guid DocId,
    string FromPath,
    string ToPath,
    string AgentId,
    string? GcId,
    string? TaskId,
    VaultCaller Caller);
