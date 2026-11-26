namespace Bazaar.Server.CodingReview;

/// <summary>
/// One review's outcome as data for doc 11 (peer review and reputation). Doc 11 weights it with hard outcomes (CI, reverts, owner
/// accept or reject). This module only records it and does not build reputation.
/// </summary>
public sealed record ReviewOutcome(
    ReviewPullRequestRef PullRequest,
    string AuthorModelId,
    string? ReviewerModelId,
    bool SameFamily,
    ReviewVerdict Verdict,
    bool ContextTruncated,
    DateTimeOffset RecordedAt);
