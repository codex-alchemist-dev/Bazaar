namespace Bazaar.Server.CodingReview;

/// <summary>
/// The PR under review and the two commits it compares. Commit ids are server-known values, not third-party text.
/// </summary>
public sealed record ReviewPullRequestRef(string Repository, int Number, string BaseCommit, string HeadCommit);
