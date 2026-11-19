using System.Security.Cryptography;
using System.Text;
using Bazaar.Server.Memory.Access;
using Bazaar.Server.Memory.Notes;
using Bazaar.Server.Memory.Retrieval;
using Bazaar.Server.Memory.Tools;
using Bazaar.Server.Memory.Write;
using Bazaar.Server.Vault.Git;
using Bazaar.Server.Vault.Writes;

namespace Bazaar.Server.Memory.GitVault;

/// <summary>
/// <see cref="IMemoryVault"/> over the Vault's git repository (docs/design/08-memory-system.md, section 11). Reads come from
/// the head commit of the main checkout. A batch is one commit on its own branch, merged through <see cref="IVaultWriter"/>,
/// which keeps one commit at a time per vault and records the trailers.
/// </summary>
public sealed class GitMemoryVault : IMemoryVault, IDisposable
{
    private static readonly string[] _noteAreas = ["agents/", "shared/"];
    private static readonly UTF8Encoding _utf8 = new(false);

    private readonly IGitProvider _git;
    private readonly IVaultWriter _writer;
    private readonly IMemoryLexicalIndex _lexical;
    private readonly GitMemoryVaultOptions _options;
    private readonly SemaphoreSlim _idMapGate = new(1, 1);
    private (string Commit, IReadOnlyDictionary<string, VaultPath> Map)? _idMap;

    public GitMemoryVault(IGitProvider git, IVaultWriter writer, IMemoryLexicalIndex lexical, GitMemoryVaultOptions options)
    {
        _git = git ?? throw new ArgumentNullException(nameof(git));
        _writer = writer ?? throw new ArgumentNullException(nameof(writer));
        _lexical = lexical ?? throw new ArgumentNullException(nameof(lexical));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        ArgumentException.ThrowIfNullOrWhiteSpace(options.RepoPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.WorktreeRoot);
        ArgumentNullException.ThrowIfNull(options.AuthorFor);
    }

    /// <summary>Releases the note-id map lock.</summary>
    public void Dispose() => _idMapGate.Dispose();

    /// <inheritdoc />
    public async Task<string?> ReadNoteAsync(VaultPath path, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(path);
        var head = await _git.HeadAsync(_options.RepoPath, ct).ConfigureAwait(false);
        try
        {
            return await _git.ReadTextAsync(_options.RepoPath, head, path.Value, ct).ConfigureAwait(false);
        }
        catch (FileNotFoundException)
        {
            return null;
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<VaultPath>> ListAsync(string areaPrefix, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(areaPrefix);
        var head = await _git.HeadAsync(_options.RepoPath, ct).ConfigureAwait(false);
        var files = await _git.ListFilesAsync(_options.RepoPath, head, ct).ConfigureAwait(false);
        var paths = new List<VaultPath>();
        foreach (var file in files)
        {
            if (file.StartsWith(areaPrefix, StringComparison.Ordinal) && VaultPath.TryParse(file, out var parsed))
            {
                paths.Add(parsed);
            }
        }

        return paths;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<RankedHit>> SearchLexicalAsync(string queryText, string areaPrefix, int maxCandidates, CancellationToken ct) =>
        _lexical.SearchAsync(queryText, areaPrefix, maxCandidates, ct);

    /// <inheritdoc />
    public async Task<VaultPath?> FindNoteByIdAsync(string noteId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(noteId);
        var head = await _git.HeadAsync(_options.RepoPath, ct).ConfigureAwait(false);

        await _idMapGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_idMap is not { } cached || cached.Commit != head)
            {
                cached = (head, await BuildIdMapAsync(head, ct).ConfigureAwait(false));
                _idMap = cached;
            }

            return cached.Map.TryGetValue(noteId, out var path) ? path : null;
        }
        finally
        {
            _idMapGate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<MemoryCommitResult> CommitAsync(MemoryBatch batch, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(batch);
        if (batch.Items.Count == 0)
        {
            return MemoryCommitResult.Failure("The batch has no writes.");
        }

        if (!VaultPath.IsValidIdentifier(batch.AgentId))
        {
            return MemoryCommitResult.Failure("The agent id is not a valid identifier.");
        }

        var prefix = $"agents/{batch.AgentId}/memory/";
        var targets = new List<(string Relative, string Content)>();
        foreach (var item in batch.Items)
        {
            if (!VaultPath.TryParse(item.Path, out var path) || !path.Value.StartsWith(prefix, StringComparison.Ordinal))
            {
                // Nothing is written when any path is outside the agent's own private memory area.
                return MemoryCommitResult.Failure($"A write is outside {prefix}.");
            }

            targets.Add((path.Value, item.Content));
        }

        var branch = $"agent/memory-{batch.AgentId}-{batch.Items[0].WriteId:N}";
        var worktree = Path.Combine(_options.WorktreeRoot, branch.Replace('/', '-'));
        var baseCommit = await _git.HeadAsync(_options.RepoPath, ct).ConfigureAwait(false);

        try
        {
            Directory.CreateDirectory(_options.WorktreeRoot);
            await _git.AddWorktreeAsync(_options.RepoPath, branch, worktree, ct).ConfigureAwait(false);
            foreach (var (relative, content) in targets)
            {
                var full = Path.Combine(worktree, relative.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                await File.WriteAllTextAsync(full, content, _utf8, ct).ConfigureAwait(false);
            }

            await _git.CommitAsync(worktree, MemoryCommitMessage.Build(batch), _options.AuthorFor(batch.AgentId), ct).ConfigureAwait(false);

            var request = new VaultEditRequest(
                DocIdFor(batch.AgentId),
                baseCommit,
                branch,
                batch.AgentId,
                NoneToNull(batch.GcId),
                NoneToNull(batch.TaskId));
            var result = await _writer.SubmitAsync(request, ct).ConfigureAwait(false);

            return result.Outcome switch
            {
                VaultEditOutcome.Merged or VaultEditOutcome.UpToDate => MemoryCommitResult.Success(result.NewCommit),
                VaultEditOutcome.Conflicted => MemoryCommitResult.Failure($"Conflict on {string.Join(", ", result.ConflictPaths)}. The branch {branch} is kept."),
                _ => MemoryCommitResult.Failure($"The Vault refused the batch ({string.Join(", ", result.ConflictPaths)})."),
            };
        }
        finally
        {
            CleanUp(worktree);
            await _git.PruneWorktreesAsync(_options.RepoPath, CancellationToken.None).ConfigureAwait(false);
        }
    }

    // A fixed document id per agent makes the writer queue one agent's batches one at a time.
    private static Guid DocIdFor(string agentId) =>
        new(SHA256.HashData(_utf8.GetBytes("bazaar-memory:" + agentId))[..16]);

    private static string? NoneToNull(string value) => value == "none" ? null : value;

    private static void CleanUp(string worktree)
    {
        try
        {
            if (Directory.Exists(worktree))
            {
                foreach (var file in Directory.EnumerateFiles(worktree, "*", SearchOption.AllDirectories))
                {
                    File.SetAttributes(file, FileAttributes.Normal);
                }

                Directory.Delete(worktree, recursive: true);
            }
        }
        catch (IOException)
        {
            // A locked directory is pruned on a later batch; the commit result stands.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private async Task<IReadOnlyDictionary<string, VaultPath>> BuildIdMapAsync(string head, CancellationToken ct)
    {
        var map = new Dictionary<string, VaultPath>(StringComparer.Ordinal);
        var files = await _git.ListFilesAsync(_options.RepoPath, head, ct).ConfigureAwait(false);
        foreach (var file in files)
        {
            if (!file.EndsWith(".md", StringComparison.Ordinal)
                || !_noteAreas.Any(area => file.StartsWith(area, StringComparison.Ordinal))
                || !VaultPath.TryParse(file, out var path))
            {
                continue;
            }

            var text = await _git.ReadTextAsync(_options.RepoPath, head, file, ct).ConfigureAwait(false);
            var parsed = NoteDocument.Parse(text);
            if (parsed.IsSuccess)
            {
                map.TryAdd(parsed.Document!.Note.Id, path);
            }
        }

        return map;
    }
}
