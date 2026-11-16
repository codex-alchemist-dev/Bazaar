namespace Bazaar.Server.Git;

/// <summary>
/// The forge contract that GitHub and GitLab both implement (doc 21, provider scope, owner 2026-11-16). It is the renamed
/// <c>IGitHubAdapter</c> from doc 21, provider-neutral. The server calls it; clients never do.
/// </summary>
public interface IForgeProvider
{
    /// <summary>The provider id this implementation serves, for example <c>github</c>.</summary>
    string ProviderId { get; }

    /// <summary>Opens a draft PR. Searches by head branch first, so a retry does not open a second one.</summary>
    Task<PrRef> OpenDraftPrAsync(OpenPrRequest request, CancellationToken cancellationToken);

    Task<IReadOnlyList<PrFile>> GetPrFilesAsync(PrRef pr, string headSha, CancellationToken cancellationToken);

    Task PostReviewAsync(PrRef pr, ReviewRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// Squash-merges a PR. Only the server calls this, and only after a signed owner approval (doc 21, Tier 2). Agent
    /// messages never trigger it.
    /// </summary>
    Task SquashMergeAsync(PrRef pr, MergeRequest request, CancellationToken cancellationToken);
}

/// <summary>A request to open a draft PR from <paramref name="Head"/> into <paramref name="Base"/>.</summary>
public sealed record OpenPrRequest(RepoRef Repo, BranchName Head, BranchName Base, string Title, string Body);

/// <summary>One changed file in a PR. <paramref name="Status"/> is <c>added</c>, <c>modified</c>, <c>removed</c> or <c>renamed</c>.</summary>
public sealed record PrFile(string Path, string Status);

public enum ReviewEvent
{
    Comment,
    RequestChanges,
    Approve,
}

public sealed record ReviewRequest(ReviewEvent Event, string Body);

/// <summary>
/// A squash merge pinned to one head sha. The merge is void if the head moved. <paramref name="SignOff"/> is the owner's
/// <c>Signed-off-by</c> line (doc 21, round 8).
/// </summary>
public sealed record MergeRequest(string HeadSha, string CommitTitle, string SignOff);
