namespace Bazaar.Server.Git;

/// <summary>
/// The git engine seam (doc 17, "Data and interfaces"; doc 21, push). Every call runs on the host. A token comes from the
/// Identity Service and never enters a VM.
/// </summary>
public interface IGitProvider
{
    Task<WorktreeHandle> AddWorktreeAsync(RepoRef repo, BranchName branch, CancellationToken cancellationToken);

    Task<CommitId> CommitAsync(WorktreeHandle worktree, CommitRequest request, CancellationToken cancellationToken);

    Task PushAsync(WorkspaceRef workspace, BranchName branch, CancellationToken cancellationToken);
}
