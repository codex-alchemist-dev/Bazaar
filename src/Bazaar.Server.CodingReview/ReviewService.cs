namespace Bazaar.Server.CodingReview;

/// <summary>
/// Runs one review end to end (doc 17, Review flow and subagents): the context from the sources, the reviewer from the pool, the
/// read-only reviewer session, then the sink. The sink receives the report, and the approval flag is set to the verdict. Approval is
/// a flag only, never merge authority. A missing reviewer or a failed session is not approved (fail closed), and the flag is cleared.
/// </summary>
public sealed class ReviewService(
    IReviewContextSources sources,
    IReviewModelDirectory models,
    IReviewerSession reviewer,
    IReviewResultSink sink,
    ReviewContextBuilder builder,
    TimeProvider? clock = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    /// <summary>Reviews one PR for an author model and records the outcome as data for doc 11.</summary>
    public async Task<ReviewOutcome> ReviewAsync(ReviewPullRequestRef pullRequest, string authorModelId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(pullRequest);
        ArgumentException.ThrowIfNullOrWhiteSpace(authorModelId);

        var files = await sources.GetDiffAsync(pullRequest, cancellationToken).ConfigureAwait(false);
        var taskCard = await sources.GetTaskCardAsync(pullRequest, cancellationToken).ConfigureAwait(false);
        var gcThread = await sources.GetGcThreadAsync(pullRequest, cancellationToken).ConfigureAwait(false);
        var checks = await sources.GetCheckSummaryAsync(pullRequest, cancellationToken).ConfigureAwait(false);
        var context = builder.Build(pullRequest, files, taskCard, gcThread, checks);

        var pool = await models.GetPoolAsync(authorModelId, cancellationToken).ConfigureAwait(false);
        var choice = pool is null ? null : ReviewerModelChooser.Choose(pool);

        ReviewVerdict verdict;
        string body;
        if (choice is null)
        {
            verdict = ReviewVerdict.NotCompleted;
            body = "No reviewer model is available for this author. The review did not run, so the change is not approved.";
        }
        else
        {
            try
            {
                var request = new ReviewerSessionRequest(context, choice.ModelId, ReviewerToolSet.AllowedToolNames);
                var result = await reviewer.RunAsync(request, cancellationToken).ConfigureAwait(false);
                verdict = result.Approved ? ReviewVerdict.Approved : ReviewVerdict.NotApproved;
                body = result.Body;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                verdict = ReviewVerdict.NotCompleted;
                body = "The reviewer session failed, so the review did not complete and the change is not approved.";
            }
        }

        var report = new ReviewReport(pullRequest, choice?.ModelId, choice?.SameFamily ?? false, verdict, body);
        await sink.PostReviewAsync(report, cancellationToken).ConfigureAwait(false);
        await sink.SetApprovalFlagAsync(pullRequest, verdict == ReviewVerdict.Approved, cancellationToken).ConfigureAwait(false);

        return new ReviewOutcome(
            pullRequest,
            authorModelId,
            choice?.ModelId,
            choice?.SameFamily ?? false,
            verdict,
            context.Truncated,
            _clock.GetUtcNow());
    }
}
