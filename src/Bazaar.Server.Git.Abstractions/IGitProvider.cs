namespace Bazaar.Server.Git;

/// <summary>
/// The git engine seam (doc 52, doc 21 push, doc 18). Every call runs on the host under a per-repo lock, with one merge lane per
/// repo. A token comes from <see cref="IGitCredentialSource"/> and never enters a VM.
/// </summary>
public interface IGitProvider
{
    Task<WorktreeHandle> AddWorktreeAsync(RepoRef repo, BranchName branch, CancellationToken cancellationToken);

    Task<CommitId> CommitAsync(WorktreeHandle worktree, CommitRequest request, CancellationToken cancellationToken);

    Task PushAsync(WorkspaceRef workspace, BranchName branch, CancellationToken cancellationToken);

    Task FetchAsync(RepoRef repo, CancellationToken cancellationToken);

    Task<IReadOnlyList<DiffEntry>> DiffAsync(RepoRef repo, string oldSha, string newSha, CancellationToken cancellationToken);

    /// <summary>Startup recovery (spike S23): abort unfinished merges and prune stale worktrees.</summary>
    Task RecoverAsync(CancellationToken cancellationToken);
}
