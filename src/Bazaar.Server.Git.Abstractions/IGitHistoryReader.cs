namespace Bazaar.Server.Git;

/// <summary>
/// Read-only history of a repo, for views such as the Git Solar System (doc 19, doc 52). Separate from <see cref="IGitProvider"/> so a
/// reader needs no write surface.
/// </summary>
public interface IGitHistoryReader
{
    /// <summary>Branch and tag tips.</summary>
    Task<IReadOnlyList<GitRefInfo>> ListRefsAsync(RepoRef repo, CancellationToken cancellationToken);

    /// <summary>Up to <paramref name="limit"/> commits reachable from <paramref name="tipSha"/>, newest first, each with its parents and times.</summary>
    Task<IReadOnlyList<GitCommitInfo>> ReadHistoryAsync(RepoRef repo, string tipSha, int limit, CancellationToken cancellationToken);
}

/// <summary><paramref name="Kind"/> is <c>branch</c> or <c>tag</c>; <paramref name="Name"/> has no <c>refs/...</c> prefix.</summary>
public sealed record GitRefInfo(string Kind, string Name, string TipSha);

public sealed record GitCommitInfo(string Sha, IReadOnlyList<string> ParentShas, string AuthorName, DateTimeOffset AuthoredAt, DateTimeOffset CommittedAt, string Subject);
