namespace Bazaar.Server.Setup;

/// <summary>What happened when a fetch of one pinned commit ended.</summary>
public enum GitFetchStatus
{
    /// <summary>The commit was fetched and checked out into the destination, and HEAD is exactly that commit.</summary>
    Ok,

    /// <summary>The request was refused before any process ran: a bad URL, a scheme that is not allowed, a bad commit id or a bad destination.</summary>
    InvalidRequest,

    /// <summary>The git program could not be started.</summary>
    GitUnavailable,

    /// <summary>The remote could not be reached or refused the request.</summary>
    FetchFailed,

    /// <summary>The remote does not hold the pinned commit.</summary>
    CommitNotFound,

    /// <summary>The fetch ran longer than the configured limit and was stopped.</summary>
    TimedOut,

    /// <summary>The checked-out commit is not the pinned one.</summary>
    CommitMismatch,
}

/// <summary>One fetch: where from, which exact commit, and where to put the files.</summary>
/// <param name="Url">A plain git URL (https, ssh or, only when the operator allows it, file). It carries no credentials.</param>
/// <param name="Commit">The full commit id in lower-case hex (40 characters, or 64 for a SHA-256 repository). Branch and tag names are not accepted.</param>
/// <param name="Destination">An absolute path to a folder that does not exist or is empty. On success it holds the commit's files and no <c>.git</c> folder.</param>
public sealed record GitFetchRequest(string Url, string Commit, string Destination);

/// <summary>The outcome of <see cref="IGitFetcher.FetchAsync"/>. <see cref="Detail"/> is fixed text and never holds the URL or git's own output.</summary>
/// <param name="Status">What happened.</param>
/// <param name="Detail">A short fixed message for the operator, or null on success.</param>
public sealed record GitFetchResult(GitFetchStatus Status, string? Detail)
{
    /// <summary>True when <see cref="Status"/> is <see cref="GitFetchStatus.Ok"/>.</summary>
    public bool Succeeded => Status == GitFetchStatus.Ok;
}

/// <summary>
/// Fetches one pinned commit of a plain git repository into a folder (doc 45 section 1.8). There is no forge API and no git server
/// here: any URL the git CLI can read works. The commit is pinned by its full id, so a moved branch changes nothing.
/// </summary>
public interface IGitFetcher
{
    /// <summary>Fetches <see cref="GitFetchRequest.Commit"/> and checks it out into <see cref="GitFetchRequest.Destination"/>. Cancelling through <paramref name="cancellationToken"/> throws.</summary>
    Task<GitFetchResult> FetchAsync(GitFetchRequest request, CancellationToken cancellationToken = default);
}
