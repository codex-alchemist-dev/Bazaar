using Bazaar.Server.Embeddings;
using Bazaar.Server.Vault.Search;

namespace Bazaar.Server.Host.Components.Embeddings;

/// <summary>
/// The query embedder Vault search holds. It delegates to the embedder for the profile that is active now and re-resolves when the
/// indexer publishes a new activation or after <see cref="RefreshInterval"/>, so a planned profile switch takes effect without a
/// restart. With no active profile, or a provider that does not match it, <see cref="IsAvailable"/> is false and search stays lexical-only.
/// </summary>
internal sealed class ActiveVaultQueryEmbedder(
    IActiveEmbeddingSelector selector,
    IEmbeddingStatus status,
    TimeProvider time,
    Action<Exception>? onMismatch = null) : IVaultQueryEmbedder
{
    public static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(60);

    private readonly Lock _gate = new();
    private VaultQueryEmbedder? _current;
    private DateTimeOffset? _seenActivation;
    private DateTimeOffset _resolvedAt = DateTimeOffset.MinValue;
    private bool _resolvedOnce;

    public bool IsAvailable => Current() is not null;

    public Guid ProfileId => Current()?.ProfileId ?? Guid.Empty;

    public Task<float[]> EmbedAsync(string text, CancellationToken cancellationToken)
    {
        var embedder = Current() ?? throw new InvalidOperationException("No embedding profile is active; search is lexical-only.");
        return embedder.EmbedAsync(text, cancellationToken);
    }

    private VaultQueryEmbedder? Current()
    {
        lock (_gate)
        {
            // The status read is an in-memory snapshot (no query), so checking it on every call is cheap.
            var activation = status.GetAsync(CancellationToken.None).GetAwaiter().GetResult().LastActivatedAt;
            var now = time.GetUtcNow();
            if (!_resolvedOnce || activation != _seenActivation || now - _resolvedAt >= RefreshInterval)
            {
                Resolve(activation, now);
            }

            return _current;
        }
    }

    private void Resolve(DateTimeOffset? activation, DateTimeOffset now)
    {
        _resolvedOnce = true;
        _seenActivation = activation;
        _resolvedAt = now;
        try
        {
            var active = selector.ResolveAsync(CancellationToken.None).GetAwaiter().GetResult();
            _current = active is null ? null : new VaultQueryEmbedder(active.Profile, active.Provider);
        }
        catch (EmbeddingException ex)
        {
            _current = null;
            onMismatch?.Invoke(ex);
        }
    }
}
