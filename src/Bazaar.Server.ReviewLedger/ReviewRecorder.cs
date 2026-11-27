using System.Text.Json;
using Bazaar.Server.ReviewLedger.Abstractions;

namespace Bazaar.Server.ReviewLedger;

/// <summary>The default <see cref="IReviewRecorder"/>.</summary>
public sealed class ReviewRecorder(IReviewStore store, ReviewCaptureSettings settings, TimeProvider time) : IReviewRecorder
{
    public Task<ReviewRecordResult> RecordAsync(ReviewRecord review, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(review);
        ArgumentException.ThrowIfNullOrWhiteSpace(review.PrRef);
        ArgumentException.ThrowIfNullOrWhiteSpace(review.ReviewerAgentId);
        ArgumentException.ThrowIfNullOrWhiteSpace(review.AuthorAgentId);
        ArgumentException.ThrowIfNullOrWhiteSpace(review.Tag);
        if (review.Overall is < 1 or > 5)
        {
            throw new ArgumentOutOfRangeException(nameof(review), review.Overall, "Overall must be 1 to 5.");
        }

        if (review.RubricJson is { } rubric)
        {
            using var doc = JsonDocument.Parse(rubric);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new ArgumentException("The rubric must be a JSON object.", nameof(review));
            }
        }

        if (string.Equals(review.ReviewerAgentId, review.AuthorAgentId, StringComparison.Ordinal))
        {
            return Task.FromResult(new ReviewRecordResult(null, ReviewRefusal.SelfReview));
        }

        return store.AppendAsync(review, settings.MaxReviewersPerPr, time.GetUtcNow(), cancellationToken);
    }
}
