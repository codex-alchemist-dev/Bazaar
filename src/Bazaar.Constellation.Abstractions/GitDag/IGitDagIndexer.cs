namespace Bazaar.Server.Constellation.GitDag;

/// <summary>
/// Indexes the commit DAG reachable from a tip, for the Git Solar System (docs/design/19, Git Solar System). Implementations
/// read history through the Vault git provider, so callers never shell out to git themselves.
/// </summary>
public interface IGitDagIndexer
{
    /// <summary>
    /// Indexes every commit reachable from <paramref name="tipCommitish"/> (a branch, tag or SHA). A null or blank tip means
    /// the repository has no commits yet, so the result is empty. Throws when the tip does not resolve.
    /// </summary>
    Task<GitDagIndex> IndexAsync(string repoPath, string? tipCommitish, CancellationToken ct);
}
