namespace Bazaar.Server.CodingReview;

/// <summary>
/// Port: the review's read-only inputs. The server implements it over the PR, the task service, the GC log and the check runner.
/// Each call returns what the server holds. Null means the source has no value, and the builder records that as missing context.
/// </summary>
public interface IReviewContextSources
{
    /// <summary>The PR's changed files as per-file hunks.</summary>
    Task<IReadOnlyList<DiffFile>> GetDiffAsync(ReviewPullRequestRef pullRequest, CancellationToken cancellationToken);

    /// <summary>The task card the PR serves, or null when the PR is not tied to a card.</summary>
    Task<TaskCardSummary?> GetTaskCardAsync(ReviewPullRequestRef pullRequest, CancellationToken cancellationToken);

    /// <summary>The GC thread that asked for the work, or null when there is none.</summary>
    Task<GcThreadExcerpt?> GetGcThreadAsync(ReviewPullRequestRef pullRequest, CancellationToken cancellationToken);

    /// <summary>The check runner's result for the head commit, or null when no run exists.</summary>
    Task<ReviewCheckSummary?> GetCheckSummaryAsync(ReviewPullRequestRef pullRequest, CancellationToken cancellationToken);
}
