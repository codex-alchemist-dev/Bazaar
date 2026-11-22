namespace Bazaar.Server.Vault.Git;

/// <summary>
/// The Vault calls the Time Machine needs (doc 18, "Time travel and fork support"; doc 26 section 4). Mapping a GC
/// sequence number to a time stays on the GC side; the Vault only knows commit times.
/// </summary>
public interface IVaultTimeTravel
{
    /// <summary>
    /// Returns the SHA of the newest commit on the main line (reachable from the default branch head) whose committer
    /// time is at or before <paramref name="at"/>, or null when there is none. Ties go to the commit found first in
    /// topological order, newest first.
    /// </summary>
    Task<string?> NearestCommitAtOrBeforeAsync(string repoPath, DateTimeOffset at, CancellationToken ct);

    /// <summary>
    /// Creates a branch ref at <paramref name="commitSha"/>. No worktree and no checkout are made, so the main checkout
    /// is untouched. Throws <see cref="InvalidOperationException"/> when the branch exists and
    /// <see cref="ArgumentException"/> when the commit is not in the repository or the branch name is not a valid
    /// branch name.
    /// </summary>
    Task CreateBranchFromCommitAsync(string repoPath, string branch, string commitSha, CancellationToken ct);
}
