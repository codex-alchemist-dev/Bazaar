namespace Bazaar.Server.Vault.Git;

/// <summary>
/// The git engine behind the Vault (doc 18, decision C12). The Vault's source of truth is a git repository of Markdown
/// files; this interface hides the engine so a CLI implementation can be added later without changing callers.
/// </summary>
/// <remarks>
/// <para>
/// <b>Single-writer rule.</b> A checkout (the main checkout or a worktree) has exactly one writer at a time. Engines
/// do not make concurrent writers safe: S23 measured lost commits, dirty checkouts and damaged merge state when writers
/// shared one checkout without serialisation, while the object database stayed intact. Every write method here takes
/// the lock for the checkout it writes to, so writers inside one process are serialised per checkout.
/// </para>
/// <para>
/// <b>Merge lane.</b> All merges into the main checkout (and the abort, add, prune and recovery calls that change its
/// state) share the lock of that checkout, so they run one at a time. Commits in a worktree take that worktree's lock and
/// may run in parallel with merges into other checkouts. Callers that write from several processes must add a
/// host-level lock on top; these locks are in-process only.
/// </para>
/// <para>
/// <b>Startup.</b> Call <see cref="RecoverAsync"/> for each Vault repository before accepting writes. It aborts merges
/// left unfinished by a crash and prunes worktrees whose directories no longer exist. Branches are never deleted.
/// </para>
/// <para>
/// <b>Not supported:</b> sparse worktrees. LibGit2Sharp has no sparse-checkout API (S23, A4), so
/// <see cref="AddWorktreeAsync"/> always materialises the whole tree. Privacy for per-agent areas stays with the Vault
/// read layer until doc 18 decides the sparse-worktree path.
/// </para>
/// </remarks>
public interface IGitProvider
{
    /// <summary>
    /// Creates a worktree at <paramref name="worktreePath"/> checked out to <paramref name="branch"/>. The branch is
    /// created from HEAD when it does not exist. Returns the full path of the new worktree.
    /// </summary>
    Task<string> AddWorktreeAsync(string repoPath, string branch, string worktreePath, CancellationToken ct);

    /// <summary>Lists the worktrees of a repository, including stale ones whose directories are gone.</summary>
    Task<IReadOnlyList<WorktreeInfo>> ListWorktreesAsync(string repoPath, CancellationToken ct);

    /// <summary>
    /// Removes the administrative data of stale worktrees (directory missing, not locked). Returns the names pruned.
    /// Worktree directories that still exist are never touched.
    /// </summary>
    Task<IReadOnlyList<string>> PruneWorktreesAsync(string repoPath, CancellationToken ct);

    /// <summary>
    /// Stages every change in the checkout (additions, edits and deletions, ignored files excluded) and commits it on the
    /// checked-out branch. Returns the new commit SHA. Fails with an engine exception when nothing has changed.
    /// </summary>
    Task<string> CommitAsync(string checkoutPath, string message, GitIdentity author, CancellationToken ct);

    /// <summary>
    /// Merges <paramref name="branch"/> into the checkout's current branch, always recording a merge commit when the
    /// merge is clean (no fast-forward). On conflicts nothing is committed: the conflicting paths are reported, the
    /// working tree keeps conflict markers, and the merge stays pending until <see cref="AbortMergeAsync"/> is called.
    /// </summary>
    /// <param name="message">
    /// The merge commit message. Null keeps the engine's default ("Merge branch ..."). Writers pass trailers here.
    /// </param>
    Task<MergeResult> MergeAsync(string checkoutPath, string branch, GitIdentity author, CancellationToken ct, string? message = null);

    /// <summary>Returns the commit SHA that HEAD of the checkout points to.</summary>
    Task<string> HeadAsync(string checkoutPath, CancellationToken ct);

    /// <summary>
    /// Abandons a pending merge: restores the checkout to its HEAD (index and working tree) and removes the merge state.
    /// The merged branch is kept. Does nothing when no merge is pending.
    /// </summary>
    Task AbortMergeAsync(string checkoutPath, CancellationToken ct);

    /// <summary>Lists the files that differ between two commits.</summary>
    Task<IReadOnlyList<DiffEntry>> DiffAsync(string repoPath, string oldSha, string newSha, CancellationToken ct);

    /// <summary>
    /// Startup recovery: aborts merges left pending in the main checkout and in each worktree, then prunes stale
    /// worktrees. Safe to run repeatedly.
    /// </summary>
    Task<RecoveryReport> RecoverAsync(string repoPath, CancellationToken ct);
}

/// <summary>The author and committer identity written to commits made by the provider.</summary>
public sealed record GitIdentity(string Name, string Email);

/// <summary>A worktree of a repository. <see cref="Branch"/> is null when HEAD is detached.</summary>
public sealed record WorktreeInfo(string Name, string Path, string? Branch, bool IsLocked, bool IsStale);

public enum GitMergeOutcome
{
    /// <summary>The branch is already contained in HEAD; nothing changed.</summary>
    UpToDate,

    /// <summary>The merge completed and a commit was recorded.</summary>
    Merged,

    /// <summary>The merge stopped with conflicts. Nothing is committed and the merge is pending.</summary>
    Conflicted,
}

/// <param name="Outcome">What the merge did.</param>
/// <param name="CommitSha">The commit at HEAD after the merge; null when conflicted.</param>
/// <param name="ConflictPaths">Repository-relative paths with conflicts, ordinal-sorted; empty unless conflicted.</param>
public sealed record MergeResult(GitMergeOutcome Outcome, string? CommitSha, IReadOnlyList<string> ConflictPaths);

public enum DiffChangeKind
{
    Added,
    Deleted,
    Modified,
    Renamed,
    Copied,
    TypeChanged,
}

/// <param name="Path">The path in the new commit (or the old one for deletions).</param>
/// <param name="OldPath">The previous path for renames and copies; otherwise null.</param>
public sealed record DiffEntry(string Path, string? OldPath, DiffChangeKind Kind);

/// <param name="AbortedMerges">Checkout paths whose unfinished merges were aborted.</param>
/// <param name="PrunedWorktrees">Names of stale worktrees removed.</param>
public sealed record RecoveryReport(IReadOnlyList<string> AbortedMerges, IReadOnlyList<string> PrunedWorktrees);
