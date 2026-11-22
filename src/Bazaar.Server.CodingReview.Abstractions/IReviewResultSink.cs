namespace Bazaar.Server.CodingReview;

/// <summary>The verdict a reviewer reached, or the reason it reached none.</summary>
public enum ReviewVerdict
{
    /// <summary>The reviewer approved the change. This is a flag on the card, never merge authority.</summary>
    Approved,

    /// <summary>The reviewer did not approve.</summary>
    NotApproved,

    /// <summary>No reviewer ran (no pool, or the session failed). Treated as not approved.</summary>
    NotCompleted,
}

/// <summary>One review to post to the PR's GC.</summary>
public sealed record ReviewReport(
    ReviewPullRequestRef PullRequest,
    string? ReviewerModelId,
    bool SameFamily,
    ReviewVerdict Verdict,
    string Body);

/// <summary>
/// Port: where a review lands. It posts the review to the PR's GC and sets the reviewer-approval flag on the card.
/// The flag is not merge authority (doc 17, C28). This interface has no merge method on purpose, and a test asserts it.
/// </summary>
public interface IReviewResultSink
{
    /// <summary>Posts the review to the PR's GC thread.</summary>
    Task PostReviewAsync(ReviewReport report, CancellationToken cancellationToken);

    /// <summary>Sets or clears the reviewer-approval flag on the card tied to the PR.</summary>
    Task SetApprovalFlagAsync(ReviewPullRequestRef pullRequest, bool approved, CancellationToken cancellationToken);
}
