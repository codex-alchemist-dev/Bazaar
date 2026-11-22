namespace Bazaar.Server.CodingReview;

/// <summary>Size caps for the review context. Operator settings may tighten them. The defaults are the shipped values.</summary>
public sealed record ReviewContextLimits(int MaxDiffCharacters = 200_000, int MaxTaskCardCharacters = 4_000, int MaxGcThreadCharacters = 8_000)
{
    /// <summary>Shipped defaults.</summary>
    public static ReviewContextLimits Default { get; } = new();
}

/// <summary>
/// One reviewer's input (doc 17, Review flow and subagents): the diff as per-file hunks, the decision context, and the check results.
/// <see cref="Highlights"/> holds the test-file changes and the fake-success findings. It is computed from the full change set
/// before any truncation, so a cap never hides them. <see cref="Truncated"/> is true whenever a cap cut any content.
/// </summary>
public sealed record ReviewContext(
    ReviewPullRequestRef PullRequest,
    IReadOnlyList<ReviewCheckFlag> Highlights,
    IReadOnlyList<DiffFile> Files,
    TaskCardSummary? TaskCard,
    GcThreadExcerpt? GcThread,
    ReviewCheckSummary? Checks,
    bool Truncated,
    int OmittedFileCount);
