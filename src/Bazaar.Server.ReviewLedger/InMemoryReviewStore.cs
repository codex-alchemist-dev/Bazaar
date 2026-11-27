using Bazaar.Server.ReviewLedger.Abstractions;

namespace Bazaar.Server.ReviewLedger;

/// <summary>In-memory review store for tests and databaseless hosts.</summary>
public sealed class InMemoryReviewStore : IReviewStore
{
    private readonly Lock _gate = new();
    private readonly List<(Guid Id, ReviewRecord Review, DateTimeOffset CreatedAt)> _rows = [];

    public IReadOnlyList<(Guid Id, ReviewRecord Review, DateTimeOffset CreatedAt)> Rows
    {
        get
        {
            lock (_gate)
            {
                return [.. _rows];
            }
        }
    }

    public Task<ReviewRecordResult> AppendAsync(ReviewRecord review, int maxReviewersPerPr, DateTimeOffset createdAt, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (review.SupersedesId is { } old)
            {
                var prior = _rows.FirstOrDefault(r => r.Id == old);
                if (prior.Review is null)
                {
                    return Task.FromResult(new ReviewRecordResult(null, ReviewRefusal.SupersedesUnknown));
                }

                if (prior.Review.PrRef != review.PrRef || prior.Review.ReviewerAgentId != review.ReviewerAgentId)
                {
                    return Task.FromResult(new ReviewRecordResult(null, ReviewRefusal.SupersedesMismatch));
                }
            }
            else
            {
                var reviewers = _rows.Where(r => r.Review.PrRef == review.PrRef).Select(r => r.Review.ReviewerAgentId).Distinct().ToList();
                if (!reviewers.Contains(review.ReviewerAgentId) && reviewers.Count >= maxReviewersPerPr)
                {
                    return Task.FromResult(new ReviewRecordResult(null, ReviewRefusal.TooManyReviewers));
                }
            }

            var id = Guid.CreateVersion7();
            _rows.Add((id, review, createdAt));
            return Task.FromResult(new ReviewRecordResult(id, ReviewRefusal.None));
        }
    }
}
