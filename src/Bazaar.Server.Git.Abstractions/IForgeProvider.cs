namespace Bazaar.Server.Git;

/// <summary>
/// The forge contract that GitHub and GitLab both implement (doc 52, doc 21 provider scope). The server calls it; clients never
/// do. There is no merge method here: a squash merge is reachable only through the owner-signed merge gate (<see cref="IForgeMergeExecutor"/>).
/// </summary>
public interface IForgeProvider
{
    /// <summary>The provider id this implementation serves, for example <c>github</c>.</summary>
    string ProviderId { get; }

    /// <summary>Opens a draft PR. Searches by head branch first, so a retry does not open a second one.</summary>
    Task<PrSummary> OpenDraftPrAsync(OpenPrRequest request, CancellationToken cancellationToken);

    Task<PagedResult<PrSummary>> ListPrsAsync(RepoRef repo, ListPrsQuery query, CancellationToken cancellationToken);

    Task<PrDetail> GetPrAsync(PrRef pr, CancellationToken cancellationToken);

    Task<IReadOnlyList<PrFile>> GetPrFilesAsync(PrRef pr, string headSha, CancellationToken cancellationToken);

    Task<IReadOnlyList<CheckRun>> GetChecksAsync(PrRef pr, string headSha, CancellationToken cancellationToken);

    /// <summary>The PR's current head sha. The owner's approval is void if this moves (doc 21, Merge request path).</summary>
    Task<string> GetHeadShaAsync(PrRef pr, CancellationToken cancellationToken);

    Task PostReviewAsync(PrRef pr, ReviewRequest request, CancellationToken cancellationToken);
}

/// <summary>
/// The squash-merge call itself. Implemented by a provider module and registered next to <see cref="IForgeProvider"/>, but only the
/// merge gate in <c>Bazaar.Server.Git</c> may call it (doc 21, doc 52). Agent messages never reach it.
/// </summary>
public interface IForgeMergeExecutor
{
    string ProviderId { get; }

    Task SquashMergeAsync(PrRef pr, MergeRequest request, CancellationToken cancellationToken);
}

/// <summary>Resolves a provider by id. An unknown id is a <see cref="UnknownForgeProviderException"/>, never a fallback.</summary>
public interface IForgeRegistry
{
    IReadOnlyCollection<string> ProviderIds { get; }

    IForgeProvider Resolve(string providerId);

    IForgeMergeExecutor ResolveMergeExecutor(string providerId);
}

public sealed class UnknownForgeProviderException(string providerId)
    : Exception($"No forge provider is registered for '{providerId}'.")
{
    public string ProviderId { get; } = providerId;
}

/// <summary>The forge has no such repository or PR (or the credential cannot see it). Providers throw this for a 404; the message carries no forge text.</summary>
public sealed class ForgeNotFoundException(string message) : Exception(message);

/// <summary>The forge asked the caller to slow down. <see cref="RetryAfter"/> is the forge's hint, or null when it gave none.</summary>
public sealed class ForgeRateLimitedException(TimeSpan? retryAfter)
    : Exception("The forge rate-limited the request.")
{
    public TimeSpan? RetryAfter { get; } = retryAfter;
}

/// <summary>A request to open a draft PR from <paramref name="Head"/> into <paramref name="Base"/>.</summary>
public sealed record OpenPrRequest(RepoRef Repo, BranchName Head, BranchName Base, string Title, string Body);

public enum PrState
{
    Open,
    Merged,
    Closed,
}

public sealed record PrSummary(PrRef Pr, string Title, string AuthorLogin, PrState State, bool Draft, string HeadSha, BranchName Head, BranchName Base, DateTimeOffset UpdatedAt);

public sealed record PrDetail(PrSummary Summary, string Body, int ChangedFiles, int Additions, int Deletions, IReadOnlyList<ReviewSummary> Reviews);

public sealed record ReviewSummary(string AuthorLogin, ReviewEvent Verdict, string HeadSha, DateTimeOffset SubmittedAt);

/// <summary>One changed file. <paramref name="Status"/> is <c>added</c>, <c>modified</c>, <c>removed</c> or <c>renamed</c>. <paramref name="Truncated"/> is true when the patch was cut at the size cap.</summary>
public sealed record PrFile(string Path, string Status, int Additions, int Deletions, string? Patch, bool Truncated);

public enum CheckState
{
    Pending,
    Success,
    Failure,
    Skipped,
}

public sealed record CheckRun(string Name, CheckState State, string HeadSha, string? DetailsUrl);

public sealed record ListPrsQuery(PrState? State, int Page, int PageSize);

public sealed record PagedResult<T>(IReadOnlyList<T> Items, bool HasMore);

public enum ReviewEvent
{
    Comment,
    RequestChanges,
    Approve,
}

public sealed record ReviewRequest(string HeadSha, ReviewEvent Event, string Body);

/// <summary>
/// A squash merge pinned to one head sha. The merge is void if the head moved. <paramref name="SignOff"/> is the owner's
/// <c>Signed-off-by</c> line (doc 21, round 8).
/// </summary>
public sealed record MergeRequest(string HeadSha, string CommitTitle, string SignOff);
