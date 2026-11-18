using LibGit2Sharp;

namespace Bazaar.Server.Vault.Git;

/// <summary>
/// LibGit2Sharp operations the offline merge needs and <see cref="IGitProvider"/> does not offer (doc 18, "Offline
/// merge"): ancestry checks, a merge of a commit by SHA, fast-forwards, ref creation and worktree removal. Kept internal
/// to the Vault module so the provider interface (and its fakes elsewhere) stays unchanged.
/// </summary>
/// <remarks>
/// Writes take <see cref="CheckoutLocks"/> for the checkout they change, as the provider does: ref and worktree changes
/// take the repository's (main checkout's) lock, the merge takes the worktree's lock.
/// </remarks>
internal static class OfflineMergeGit
{
    private const string _branchPrefix = "refs/heads/";

    /// <summary>The branch checked out in the main checkout, or null when HEAD is detached.</summary>
    public static Task<string?> CurrentBranchAsync(string repoPath, CancellationToken ct) =>
        Task.Run(() =>
        {
            using var repo = Open(repoPath);
            return repo.Info.IsHeadDetached ? null : repo.Head.FriendlyName;
        }, ct);

    /// <summary>The tip SHA of local branch <paramref name="branch"/>, or null when it does not exist.</summary>
    public static Task<string?> BranchTipAsync(string repoPath, string branch, CancellationToken ct) =>
        Task.Run(() =>
        {
            using var repo = Open(repoPath);
            return repo.Branches[branch] is { IsRemote: false } found ? found.Tip?.Sha : null;
        }, ct);

    /// <summary>
    /// True when <paramref name="branch"/> (with or without <c>refs/heads/</c>) is a git-valid local branch name of at most
    /// 200 characters, with no whitespace or control characters and every segment passing <see cref="IsSafePath"/>.
    /// </summary>
    internal static bool IsValidSourceBranch(string? branch)
    {
        if (string.IsNullOrEmpty(branch))
        {
            return false;
        }

        var name = ShortBranchName(branch);
        return name.Length is > 0 and <= 200
            && !name.Any(c => char.IsControl(c) || char.IsWhiteSpace(c))
            && IsSafePath(name)
            && Reference.IsValidName(_branchPrefix + name);
    }

    /// <summary>True when <paramref name="sha"/> names a commit in the object database.</summary>
    public static Task<bool> CommitExistsAsync(string repoPath, string sha, CancellationToken ct) =>
        Task.Run(() =>
        {
            using var repo = Open(repoPath);
            return repo.Lookup(sha) is Commit;
        }, ct);

    /// <summary>True when <paramref name="ancestorSha"/> equals or is an ancestor of <paramref name="descendantSha"/>.</summary>
    public static Task<bool> IsAncestorAsync(string repoPath, string ancestorSha, string descendantSha, CancellationToken ct) =>
        Task.Run(() =>
        {
            if (string.Equals(ancestorSha, descendantSha, StringComparison.Ordinal))
            {
                return true;
            }

            using var repo = Open(repoPath);
            var ancestor = repo.Lookup<Commit>(ancestorSha);
            var descendant = repo.Lookup<Commit>(descendantSha);
            if (ancestor is null || descendant is null)
            {
                return false;
            }

            return string.Equals(repo.ObjectDatabase.FindMergeBase(ancestor, descendant)?.Sha, ancestor.Sha, StringComparison.Ordinal);
        }, ct);

    /// <summary>
    /// Every changed entry between two commits that the Vault refuses: an unsafe path (see <see cref="IsSafePath"/>), a
    /// symlink or a submodule. Ordinal-sorted, each with the reason.
    /// </summary>
    public static Task<IReadOnlyList<string>> FindUnsafeChangesAsync(string repoPath, string oldSha, string newSha, CancellationToken ct) =>
        Task.Run(() =>
        {
            using var repo = Open(repoPath);
            var oldTree = repo.Lookup<Commit>(oldSha)?.Tree ?? throw new ArgumentException($"Commit '{oldSha}' not found.", nameof(oldSha));
            var newTree = repo.Lookup<Commit>(newSha)?.Tree ?? throw new ArgumentException($"Commit '{newSha}' not found.", nameof(newSha));
            var found = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var change in repo.Diff.Compare<TreeChanges>(oldTree, newTree))
            {
                foreach (var path in new[] { change.Path, change.OldPath })
                {
                    if (path is not null && !IsSafePath(path))
                    {
                        found.Add($"unsafe path '{Printable(path)}'");
                    }
                }

                if (change.Mode is Mode.SymbolicLink or Mode.GitLink)
                {
                    found.Add($"{(change.Mode == Mode.SymbolicLink ? "symlink" : "submodule")} '{Printable(change.Path)}'");
                }
            }

            return (IReadOnlyList<string>)[.. found];
        }, ct);

    /// <summary>
    /// A relative '/'-separated path with no empty, '.', '..' or '.git' segment (any case), no backslash, colon or control
    /// character. Matches the writer's traversal rule, without its '.md' requirement (the Vault may hold other files).
    /// </summary>
    internal static bool IsSafePath(string path)
    {
        if (path.Length == 0 || path[0] == '/' || Path.IsPathRooted(path))
        {
            return false;
        }

        if (path.Any(c => c == '\\' || c == ':' || char.IsControl(c)))
        {
            return false;
        }

        return path.Split('/').All(part =>
            part.Length > 0 && part is not "." and not ".." && !string.Equals(part, ".git", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Fast-forwards the checkout's branch to <paramref name="sha"/> and updates its working tree. Throws when the move is
    /// not a fast-forward.
    /// </summary>
    public static async Task FastForwardAsync(string checkoutPath, string sha, GitIdentity identity, CancellationToken ct)
    {
        using var _ = await CheckoutLocks.AcquireAsync(checkoutPath, ct).ConfigureAwait(false);
        await Task.Run(() =>
        {
            using var repo = Open(checkoutPath);
            var target = repo.Lookup<Commit>(sha) ?? throw new ArgumentException($"Commit '{sha}' not found.", nameof(sha));
            var result = repo.Merge(target, Signature(identity), new MergeOptions { FastForwardStrategy = FastForwardStrategy.FastForwardOnly });
            if (result.Status is not (MergeStatus.FastForward or MergeStatus.UpToDate))
            {
                throw new InvalidOperationException($"Fast-forward to '{sha}' ended with status {result.Status}.");
            }
        }, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Merges commit <paramref name="sha"/> into the worktree's branch (three-way, diff3 style, rename detection on, never
    /// a fast-forward). A clean merge is committed with <paramref name="message"/>; a conflicted one is left pending in the
    /// worktree and its paths returned.
    /// </summary>
    public static async Task<WorktreeMergeResult> MergeCommitAsync(
        string worktreePath,
        string sha,
        GitIdentity identity,
        string message,
        CancellationToken ct)
    {
        using var _ = await CheckoutLocks.AcquireAsync(worktreePath, ct).ConfigureAwait(false);
        return await Task.Run(() =>
        {
            using var repo = Open(worktreePath);
            var incoming = repo.Lookup<Commit>(sha) ?? throw new ArgumentException($"Commit '{sha}' not found.", nameof(sha));
            var signature = Signature(identity);
            var options = new MergeOptions
            {
                FastForwardStrategy = FastForwardStrategy.NoFastForward,
                CommitOnSuccess = false,
                FailOnConflict = false,
                FindRenames = true,
                FileConflictStrategy = CheckoutFileConflictStrategy.Diff3,
            };
            var merge = repo.Merge(incoming, signature, options);
            if (merge.Status == MergeStatus.Conflicts)
            {
                return new WorktreeMergeResult(null, ConflictPaths(repo));
            }

            var commit = repo.Commit(message, signature, signature, new CommitOptions { AllowEmptyCommit = true });
            return new WorktreeMergeResult(commit.Sha, []);
        }, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Creates <c>&lt;prefix&gt;&lt;stamp&gt;</c> at <paramref name="sha"/>, adding <c>-2</c>, <c>-3</c> on a clash. When a
    /// branch under <paramref name="prefix"/> already points at <paramref name="sha"/>, returns that one instead.
    /// </summary>
    public static async Task<string> CreateConflictBranchAsync(string repoPath, string prefix, string stamp, string sha, CancellationToken ct)
    {
        using var _ = await CheckoutLocks.AcquireAsync(repoPath, ct).ConfigureAwait(false);
        return await Task.Run(() =>
        {
            using var repo = Open(repoPath);
            var existing = repo.Branches
                .Where(b => !b.IsRemote && b.FriendlyName.StartsWith(prefix, StringComparison.Ordinal)
                    && string.Equals(b.Tip?.Sha, sha, StringComparison.Ordinal))
                .Select(b => b.FriendlyName)
                .Order(StringComparer.Ordinal)
                .FirstOrDefault();
            if (existing is not null)
            {
                return existing;
            }

            var target = repo.Lookup<Commit>(sha) ?? throw new ArgumentException($"Commit '{sha}' not found.", nameof(sha));
            var name = prefix + stamp;
            for (var suffix = 2; repo.Branches[name] is not null; suffix++)
            {
                name = $"{prefix}{stamp}-{suffix}";
            }

            repo.CreateBranch(name, target);
            return name;
        }, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Removes a scratch worktree completely: its directory, its administrative folder and its branch. Safe to call when
    /// some of them are already gone.
    /// </summary>
    public static async Task RemoveWorktreeAsync(string repoPath, string worktreePath, string branch, CancellationToken ct)
    {
        using var _ = await CheckoutLocks.AcquireAsync(repoPath, ct).ConfigureAwait(false);
        await Task.Run(() =>
        {
            var full = Path.GetFullPath(worktreePath);
            if (Directory.Exists(full))
            {
                foreach (var file in Directory.EnumerateFiles(full, "*", SearchOption.AllDirectories))
                {
                    File.SetAttributes(file, FileAttributes.Normal);
                }

                Directory.Delete(full, recursive: true);
            }

            using var repo = Open(repoPath);
            var admin = Path.Combine(repo.Info.Path, "worktrees", Path.GetFileName(full));
            if (Directory.Exists(admin))
            {
                Directory.Delete(admin, recursive: true);
            }

            if (repo.Branches[branch] is { } scratch)
            {
                repo.Branches.Remove(scratch);
            }
        }, ct).ConfigureAwait(false);
    }

    /// <summary>Strips <c>refs/heads/</c> from a branch name.</summary>
    public static string ShortBranchName(string branch) =>
        branch.StartsWith(_branchPrefix, StringComparison.Ordinal) ? branch[_branchPrefix.Length..] : branch;

    private static List<string> ConflictPaths(Repository repo) =>
        repo.Index.Conflicts
            .SelectMany(c => new[] { c.Ancestor?.Path, c.Ours?.Path, c.Theirs?.Path })
            .Where(p => p is not null)
            .Select(p => p!)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();

    private static string Printable(string path) =>
        new([.. path.Select(c => char.IsControl(c) ? '?' : c)]);

    private static Repository Open(string path) => new(Path.GetFullPath(path));

    private static Signature Signature(GitIdentity identity) => new(identity.Name, identity.Email, DateTimeOffset.UtcNow);
}

/// <param name="CommitSha">The merge commit, or null when the merge conflicted.</param>
/// <param name="ConflictPaths">Conflicting paths, ordinal-sorted; empty when clean.</param>
internal sealed record WorktreeMergeResult(string? CommitSha, IReadOnlyList<string> ConflictPaths);
