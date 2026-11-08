using System.Text;
using Bazaar.Server.Vault.Links;
using Bazaar.Server.Vault.Markdown;

namespace Bazaar.Server.Vault.Writes;

/// <summary>
/// Move and rename with link rewrite (doc 18, work package 8). The move runs in the main checkout under the vault lane,
/// so it lands as one commit: the file moves, and every wiki-link that resolved to it by path points at the new path.
/// </summary>
/// <remarks>
/// Only links that match by path are rewritten. A link that matched by title or alias still resolves after the move, so
/// it keeps its text. Ambiguous links are left alone; a warning would hide a real choice from the author.
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

        var index = await BuildIndexAsync(ct);
        var rewrites = await PlanLinkRewritesAsync(index, request.FromPath, request.ToPath, ct);

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

    /// <summary>Builds the index entries for every Markdown file in the main checkout, ordinal-sorted by path.</summary>
    private async Task<List<VaultIndexEntry>> BuildIndexAsync(CancellationToken ct)
    {
        var entries = new List<VaultIndexEntry>();
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
        }

        return entries.OrderBy(entry => entry.Path, StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// Works out the rewritten text of every document that links to the moved one by path. Returns the new text keyed by
    /// the path it has before the move; the moved document is included when it links to itself.
    /// </summary>
    private async Task<IReadOnlyDictionary<string, string>> PlanLinkRewritesAsync(
        List<VaultIndexEntry> index,
        string fromPath,
        string toPath,
        CancellationToken ct)
    {
        var rewrites = new Dictionary<string, string>(StringComparer.Ordinal);
        var oldTarget = StripExtension(fromPath);
        var newTarget = StripExtension(toPath);
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
                var resolution = VaultLinkResolver.Resolve(link, entry, index, _ => true, VaultAmbiguousLinkPolicy.Refuse);
                if (!resolution.Resolved || resolution.Warning is not null
                    || !string.Equals(resolution.Target?.Path, fromPath, StringComparison.Ordinal))
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

                var replacementTarget = link.Target.EndsWith(_markdownExtension, StringComparison.Ordinal) ? toPath : newTarget;
                var replaced = linkText.Remove(targetAt, link.Target.Length).Insert(targetAt, replacementTarget);
                edits.Add(new SourceEdit(link.Start, link.Length, replaced));
            }

            if (edits.Count > 0)
            {
                rewrites[entry.Path] = document.Render(edits);
            }
        }

        return rewrites;
    }

    private static bool IsFrozenDecision(string path, string text) =>
        path.StartsWith("decisions/", StringComparison.Ordinal)
        && DecisionImmutability.StatusOf(text) is { } status
        && status != "proposed";

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

/// <summary>A request to move or rename a document. Paths are relative to the Vault root, using '/'.</summary>
public sealed record VaultMoveRequest(
    Guid DocId,
    string FromPath,
    string ToPath,
    string AgentId,
    string? GcId,
    string? TaskId);
