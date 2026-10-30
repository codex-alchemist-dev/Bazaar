using LibGit2Sharp;

namespace Bazaar.Server.Vault.Git;

/// <summary>
/// The default <see cref="IGitProvider"/> (doc 18, C12; spike S23). Uses LibGit2Sharp with its bundled native libgit2,
/// so no git executable is needed on the host. Each call opens its own <see cref="Repository"/> and disposes it; the
/// engine's repository objects are not shared between threads.
/// </summary>
/// <remarks>
/// Writes take <see cref="CheckoutLocks"/> for the checkout they change (see the single-writer rule on
/// <see cref="IGitProvider"/>). Sparse worktrees are not implemented.
/// </remarks>
public sealed class LibGit2GitProvider : IGitProvider
{
    private static readonly string[] _mergeStateNames = ["MERGE_HEAD", "MERGE_MSG", "MERGE_MODE", "MERGE_RR"];

    public async Task<string> AddWorktreeAsync(string repoPath, string branch, string worktreePath, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(branch);
        ArgumentException.ThrowIfNullOrWhiteSpace(worktreePath);
        var fullWorktreePath = Path.GetFullPath(worktreePath);

        using var _ = await CheckoutLocks.AcquireAsync(repoPath, ct).ConfigureAwait(false);
        return await Task.Run(() =>
        {
            using var repo = OpenRepository(repoPath);
            var reference = repo.Branches[branch] ?? repo.CreateBranch(branch);
            repo.Worktrees.Add(reference.CanonicalName, Path.GetFileName(fullWorktreePath), fullWorktreePath, false);
            return fullWorktreePath;
        }, ct).ConfigureAwait(false);
    }

    public Task<IReadOnlyList<WorktreeInfo>> ListWorktreesAsync(string repoPath, CancellationToken ct) =>
        Task.Run(() => (IReadOnlyList<WorktreeInfo>)ReadWorktrees(repoPath), ct);

    public async Task<IReadOnlyList<string>> PruneWorktreesAsync(string repoPath, CancellationToken ct)
    {
        using var _ = await CheckoutLocks.AcquireAsync(repoPath, ct).ConfigureAwait(false);
        return await Task.Run(() => PruneStale(repoPath), ct).ConfigureAwait(false);
    }

    public async Task<string> CommitAsync(string checkoutPath, string message, GitIdentity author, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        ArgumentNullException.ThrowIfNull(author);

        using var _ = await CheckoutLocks.AcquireAsync(checkoutPath, ct).ConfigureAwait(false);
        return await Task.Run(() =>
        {
            using var repo = OpenRepository(checkoutPath);
            Commands.Stage(repo, "*");
            var signature = Signature(author);
            return repo.Commit(message, signature, signature).Sha;
        }, ct).ConfigureAwait(false);
    }

    public async Task<MergeResult> MergeAsync(string checkoutPath, string branch, GitIdentity author, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(branch);
        ArgumentNullException.ThrowIfNull(author);

        using var _ = await CheckoutLocks.AcquireAsync(checkoutPath, ct).ConfigureAwait(false);
        return await Task.Run(() =>
        {
            using var repo = OpenRepository(checkoutPath);
            var source = repo.Branches[branch] ?? throw new ArgumentException($"Branch '{branch}' does not exist.", nameof(branch));
            var options = new MergeOptions
            {
                FastForwardStrategy = FastForwardStrategy.NoFastForward,
                CommitOnSuccess = true,
            };
            var merge = repo.Merge(source, Signature(author), options);
            return merge.Status switch
            {
                MergeStatus.UpToDate => new MergeResult(GitMergeOutcome.UpToDate, repo.Head.Tip.Sha, []),
                MergeStatus.Conflicts => new MergeResult(GitMergeOutcome.Conflicted, null, ConflictPaths(repo)),
                _ => new MergeResult(GitMergeOutcome.Merged, merge.Commit.Sha, []),
            };
        }, ct).ConfigureAwait(false);
    }

    public async Task AbortMergeAsync(string checkoutPath, CancellationToken ct)
    {
        using var _ = await CheckoutLocks.AcquireAsync(checkoutPath, ct).ConfigureAwait(false);
        await Task.Run(() =>
        {
            using var repo = OpenRepository(checkoutPath);
            AbortPendingMerge(repo);
        }, ct).ConfigureAwait(false);
    }

    public Task<IReadOnlyList<DiffEntry>> DiffAsync(string repoPath, string oldSha, string newSha, CancellationToken ct) =>
        Task.Run(() =>
        {
            using var repo = OpenRepository(repoPath);
            var oldTree = repo.Lookup<Commit>(oldSha)?.Tree ?? throw new ArgumentException($"Commit '{oldSha}' not found.", nameof(oldSha));
            var newTree = repo.Lookup<Commit>(newSha)?.Tree ?? throw new ArgumentException($"Commit '{newSha}' not found.", nameof(newSha));
            var changes = repo.Diff.Compare<TreeChanges>(oldTree, newTree);
            return (IReadOnlyList<DiffEntry>)changes
                .Where(c => c.Status != ChangeKind.Unmodified)
                .Select(c => new DiffEntry(
                    c.Path,
                    c.Status is ChangeKind.Renamed or ChangeKind.Copied ? c.OldPath : null,
                    MapKind(c.Status)))
                .ToList();
        }, ct);

    public async Task<RecoveryReport> RecoverAsync(string repoPath, CancellationToken ct)
    {
        using var _ = await CheckoutLocks.AcquireAsync(repoPath, ct).ConfigureAwait(false);
        var aborted = new List<string>();

        if (await Task.Run(() => AbortIfMerging(repoPath), ct).ConfigureAwait(false))
        {
            aborted.Add(repoPath);
        }

        var livePaths = await Task.Run(() => ReadWorktrees(repoPath).Where(info => !info.IsStale).Select(info => info.Path).ToList(), ct)
            .ConfigureAwait(false);

        foreach (var worktreePath in livePaths)
        {
            using var worktreeLease = await CheckoutLocks.AcquireAsync(worktreePath, ct).ConfigureAwait(false);
            if (await Task.Run(() => AbortIfMerging(worktreePath), ct).ConfigureAwait(false))
            {
                aborted.Add(worktreePath);
            }
        }

        var pruned = await Task.Run(() => PruneStale(repoPath), ct).ConfigureAwait(false);
        return new RecoveryReport(aborted, pruned);
    }

    private static bool AbortIfMerging(string checkoutPath)
    {
        using var repo = OpenRepository(checkoutPath);
        return AbortPendingMerge(repo);
    }

    private static Repository OpenRepository(string path) => new(Path.GetFullPath(path));

    private static Signature Signature(GitIdentity author) => new(author.Name, author.Email, DateTimeOffset.UtcNow);

    private static List<string> ConflictPaths(Repository repo) =>
        repo.Index.Conflicts
            .Select(c => (c.Ours ?? c.Theirs ?? c.Ancestor)?.Path)
            .Where(p => p is not null)
            .Select(p => p!)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();

    /// <summary>
    /// Clears merge state and hard-resets the checkout to HEAD. Returns false when no merge was pending. The branch
    /// that was merged is not touched, so it can be merged again later.
    /// </summary>
    private static bool AbortPendingMerge(Repository repo)
    {
        if (repo.Info.CurrentOperation != CurrentOperation.Merge)
        {
            return false;
        }

        // libgit2 keeps the pending merge in these state files inside the git directory (per worktree for worktrees).
        foreach (var name in _mergeStateNames)
        {
            var file = Path.Combine(repo.Info.Path, name);
            if (File.Exists(file))
            {
                File.Delete(file);
            }
        }

        repo.Reset(ResetMode.Hard, repo.Head.Tip);
        return true;
    }

    /// <summary>
    /// Removes the administrative data of stale worktrees: the <c>worktrees/&lt;name&gt;</c> folder of a worktree whose
    /// directory is gone and that is not locked. This is what <c>git worktree prune</c> does. LibGit2Sharp cannot
    /// do it through its API: it returns null for such worktrees (checked against LibGit2Sharp 0.32.0).
    /// </summary>
    private static List<string> PruneStale(string repoPath)
    {
        var pruned = new List<string>();
        foreach (var info in ReadWorktrees(repoPath))
        {
            if (!info.IsStale || info.IsLocked)
            {
                continue;
            }

            Directory.Delete(Path.Combine(AdminRoot(repoPath), info.Name), recursive: true);
            pruned.Add(info.Name);
        }

        return pruned;
    }

    /// <summary>
    /// Reads every worktree from its administrative folder under the repository's git directory. This is read
    /// directly rather than through LibGit2Sharp, which returns null for worktrees whose directory is gone.
    /// </summary>
    private static List<WorktreeInfo> ReadWorktrees(string repoPath)
    {
        var root = AdminRoot(repoPath);
        if (!Directory.Exists(root))
        {
            return [];
        }

        return Directory.GetDirectories(root)
            .Order(StringComparer.Ordinal)
            .Select(Describe)
            .ToList();
    }

    private static string AdminRoot(string repoPath)
    {
        using var repo = OpenRepository(repoPath);
        return Path.Combine(repo.Info.Path, "worktrees");
    }

    private static WorktreeInfo Describe(string adminDir)
    {
        var gitFilePointer = Path.Combine(adminDir, "gitdir");
        var path = File.Exists(gitFilePointer)
            ? Path.GetDirectoryName(File.ReadAllText(gitFilePointer).Trim()) ?? string.Empty
            : string.Empty;
        var isStale = path.Length == 0 || !Directory.Exists(path);

        string? branch = null;
        var headFile = Path.Combine(adminDir, "HEAD");
        if (File.Exists(headFile))
        {
            const string prefix = "ref: refs/heads/";
            var head = File.ReadAllText(headFile).Trim();
            branch = head.StartsWith(prefix, StringComparison.Ordinal) ? head[prefix.Length..] : null;
        }

        var isLocked = File.Exists(Path.Combine(adminDir, "locked"));
        return new WorktreeInfo(Path.GetFileName(adminDir), path, branch, isLocked, isStale);
    }

    private static DiffChangeKind MapKind(ChangeKind kind) => kind switch
    {
        ChangeKind.Added => DiffChangeKind.Added,
        ChangeKind.Deleted => DiffChangeKind.Deleted,
        ChangeKind.Modified => DiffChangeKind.Modified,
        ChangeKind.Renamed => DiffChangeKind.Renamed,
        ChangeKind.Copied => DiffChangeKind.Copied,
        ChangeKind.TypeChanged => DiffChangeKind.TypeChanged,
        _ => throw new InvalidOperationException($"Unexpected diff change kind '{kind}'."),
    };
}
