namespace Bazaar.Server.CodingReview.InMemory;

/// <summary>In-memory <see cref="IReviewModelDirectory"/>: one pool per author model ID. Unknown authors get null.</summary>
public sealed class InMemoryReviewModelDirectory : IReviewModelDirectory
{
    private readonly Dictionary<string, ReviewerPool> _pools = new(StringComparer.Ordinal);

    /// <summary>Adds or replaces the pool for the pool's author model.</summary>
    public void Add(ReviewerPool pool)
    {
        ArgumentNullException.ThrowIfNull(pool);
        _pools[pool.AuthorModelId] = pool;
    }

    /// <inheritdoc />
    public Task<ReviewerPool?> GetPoolAsync(string authorModelId, CancellationToken cancellationToken) =>
        Task.FromResult(_pools.TryGetValue(authorModelId, out var pool) ? pool : null);
}
