namespace Bazaar.Server.Vault.Git;

/// <summary>
/// Creates a branch ref at a commit. Kept apart from <see cref="IGitProvider"/> so existing providers and fakes stay
/// valid; the offline merge engine needs it to keep a conflicted commit under a stable name.
/// </summary>
public interface IGitBranchWriter
{
    /// <summary>
    /// Creates <paramref name="branch"/> at <paramref name="commitSha"/>. Returns false, and changes nothing, when the
    /// branch already exists. Throws when the commit does not exist.
    /// </summary>
    Task<bool> CreateBranchAsync(string repoPath, string branch, string commitSha, CancellationToken ct);
}
