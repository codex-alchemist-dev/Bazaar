namespace Bazaar.Server.GitHub.Adapter;

/// <summary>REST and GraphQL surface the host uses for GitHub (doc 21, Design). Never called by clients.</summary>
public interface IGitHubAdapter
{
    /// <summary>Searches by head branch first, then creates a draft PR. Returns the existing PR when one is open.</summary>
    Task<PullRequestRef> OpenDraftPrAsync(OpenPrRequest request, CancellationToken cancellationToken);

    Task<IReadOnlyList<PrFile>> GetPrFilesAsync(PrRef pr, string headSha, CancellationToken cancellationToken);

    Task PostReviewAsync(PrRef pr, ReviewRequest review, CancellationToken cancellationToken);

    /// <summary>The PR's current head sha. The owner's approval is void if this moves (doc 21, Merge request path).</summary>
    Task<string> GetHeadShaAsync(PrRef pr, CancellationToken cancellationToken);

    /// <summary>
    /// The state of every check on <paramref name="headSha"/>: check runs and legacy commit statuses together. The merge gate refuses
    /// anything but <see cref="PrChecksState.Passing"/> (doc 21, Merge request path: "checks pass").
    /// </summary>
    Task<PrChecksState> GetChecksStateAsync(PrRef pr, string headSha, CancellationToken cancellationToken);

    /// <summary>
    /// One conditional read of the repository Events API (doc 21, Ingress). The caller owns the ETag so it can tell "not modified" from
    /// new events; the adapter's own GET cache is bypassed. Rate gating, retries and backoff apply as for any read.
    /// </summary>
    Task<RepositoryEventsPage> ListRepositoryEventsAsync(RepositoryEventsRequest request, CancellationToken cancellationToken);

    /// <summary>The repository's GraphQL node id. Repos are keyed by node id so renames do not break mappings (doc 21, Behaviour).</summary>
    Task<string> GetRepositoryNodeIdAsync(long installationId, string owner, string repo, CancellationToken cancellationToken);
}

/// <summary>Request for one page of repository events. <paramref name="ETag"/> is the value from the last 200, or null for an unconditional read.</summary>
public sealed record RepositoryEventsRequest(long InstallationId, string Owner, string Repo, string? ETag);

/// <summary>
/// The result of one Events API read. When <see cref="NotModified"/> is true, <see cref="Body"/> is empty and the previous ETag stays current.
/// <see cref="PollInterval"/> is GitHub's <c>X-Poll-Interval</c>, which the caller must not poll faster than.
/// </summary>
public sealed record RepositoryEventsPage(bool NotModified, string? ETag, TimeSpan? PollInterval, string Body);

/// <summary>Combined state of the checks on one commit.</summary>
public enum PrChecksState
{
    /// <summary>At least one check exists and every one finished green (success, neutral or skipped).</summary>
    Passing,

    /// <summary>At least one check failed, was cancelled, timed out or needs action.</summary>
    Failing,

    /// <summary>No check has failed, but at least one has not finished.</summary>
    Pending,

    /// <summary>The commit has no checks, so nothing can be verified. Treated as not passing.</summary>
    NoChecks,
}

public sealed record PrRef(long InstallationId, string Owner, string Repo, int Number);

public sealed record PullRequestRef(PrRef Pr, string NodeId, string HeadSha, bool Draft);

public sealed record OpenPrRequest(long InstallationId, string Owner, string Repo, string HeadBranch, string BaseBranch, string Title, string Body);

public sealed record PrFile(string Path, string Status, int Additions, int Deletions, string? Patch);

public sealed record ReviewRequest(string HeadSha, string Event, string Body);

public sealed record MergeRequest(string HeadSha, string CommitTitle, string CommitMessage);
