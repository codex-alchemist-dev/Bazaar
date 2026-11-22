namespace Bazaar.Server.Embeddings;

/// <summary>Holds the indexer's latest snapshot and serves it as <see cref="IEmbeddingStatus"/>. The indexer publishes; readers never query.</summary>
public sealed class EmbeddingStatusTracker : IEmbeddingStatus
{
    private volatile EmbeddingStatusSnapshot _snapshot = EmbeddingStatusSnapshot.Initial;

    public Task<EmbeddingStatusSnapshot> GetAsync(CancellationToken cancellationToken) => Task.FromResult(_snapshot);

    internal void Publish(EmbeddingStatusSnapshot snapshot) => _snapshot = snapshot;

    internal DateTimeOffset? LastActivatedAt => _snapshot.LastActivatedAt;
}
