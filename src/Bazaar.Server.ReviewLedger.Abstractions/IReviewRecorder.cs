namespace Bazaar.Server.ReviewLedger.Abstractions;

/// <summary>Captures peer reviews in the append-only <c>review</c> table (doc 11, work package 4).</summary>
public interface IReviewRecorder
{
    /// <summary>Validates and appends a review. Invalid input throws; a rule refusal comes back in the result.</summary>
    Task<ReviewRecordResult> RecordAsync(ReviewRecord review, CancellationToken cancellationToken);
}

/// <summary>Append-only review storage. The cap and supersede checks run inside the store so concurrent reviews for one PR cannot slip past them.</summary>
public interface IReviewStore
{
    Task<ReviewRecordResult> AppendAsync(ReviewRecord review, int maxReviewersPerPr, DateTimeOffset createdAt, CancellationToken cancellationToken);
}
