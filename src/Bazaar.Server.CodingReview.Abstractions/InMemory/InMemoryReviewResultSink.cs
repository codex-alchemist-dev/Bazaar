namespace Bazaar.Server.CodingReview.InMemory;

/// <summary>
/// In-memory <see cref="IReviewResultSink"/> that records every post and flag change in order. It has no merge operation,
/// matching the port.
/// </summary>
public sealed class InMemoryReviewResultSink : IReviewResultSink
{
    private readonly List<ReviewReport> _posted = [];
    private readonly List<(ReviewPullRequestRef PullRequest, bool Approved)> _flags = [];

    /// <summary>Reviews posted to the GC, oldest first.</summary>
    public IReadOnlyList<ReviewReport> Posted => _posted;

    /// <summary>Approval flag changes, oldest first.</summary>
    public IReadOnlyList<(ReviewPullRequestRef PullRequest, bool Approved)> Flags => _flags;

    /// <inheritdoc />
    public Task PostReviewAsync(ReviewReport report, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(report);
        _posted.Add(report);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task SetApprovalFlagAsync(ReviewPullRequestRef pullRequest, bool approved, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(pullRequest);
        _flags.Add((pullRequest, approved));
        return Task.CompletedTask;
    }
}
