namespace Bazaar.Server.CodingReview.InMemory;

/// <summary>In-memory <see cref="IReviewContextSources"/> for tests and dry runs. Set the fields a test needs; the rest stay empty.</summary>
public sealed class InMemoryReviewContextSources : IReviewContextSources
{
    /// <summary>The diff returned for every PR.</summary>
    public IReadOnlyList<DiffFile> Diff { get; set; } = [];

    /// <summary>The task card returned for every PR.</summary>
    public TaskCardSummary? TaskCard { get; set; }

    /// <summary>The GC excerpt returned for every PR.</summary>
    public GcThreadExcerpt? GcThread { get; set; }

    /// <summary>The check summary returned for every PR.</summary>
    public ReviewCheckSummary? Checks { get; set; }

    /// <inheritdoc />
    public Task<IReadOnlyList<DiffFile>> GetDiffAsync(ReviewPullRequestRef pullRequest, CancellationToken cancellationToken) =>
        Task.FromResult(Diff);

    /// <inheritdoc />
    public Task<TaskCardSummary?> GetTaskCardAsync(ReviewPullRequestRef pullRequest, CancellationToken cancellationToken) =>
        Task.FromResult(TaskCard);

    /// <inheritdoc />
    public Task<GcThreadExcerpt?> GetGcThreadAsync(ReviewPullRequestRef pullRequest, CancellationToken cancellationToken) =>
        Task.FromResult(GcThread);

    /// <inheritdoc />
    public Task<ReviewCheckSummary?> GetCheckSummaryAsync(ReviewPullRequestRef pullRequest, CancellationToken cancellationToken) =>
        Task.FromResult(Checks);
}
