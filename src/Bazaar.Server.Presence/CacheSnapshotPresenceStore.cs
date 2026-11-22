using System.Text.Json;
using System.Text.Json.Serialization;
using Bazaar.Server.Presence.Abstractions;
using Bazaar.Server.Providers;

namespace Bazaar.Server.Presence;

[JsonSourceGenerationOptions(UseStringEnumConverter = true)]
[JsonSerializable(typeof(PresenceRow))]
internal sealed partial class PresenceCacheJson : JsonSerializerContext;

/// <summary>
/// Keeps the latest row per agent in the cache provider as a best-effort snapshot cache (docs/design/23; the cache has no pub/sub,
/// so deltas travel over the wire). Writes go to the inner store first, then to the cache. A cache that is down, slow or full
/// loses the write and is otherwise ignored: presence degrades, it does not fail (doc 03, doc 23). A read the inner store
/// cannot answer falls back to the cache, which is how a restart keeps its revisions climbing.
/// </summary>
public sealed class CacheSnapshotPresenceStore(IPresenceStore inner, ICacheProvider cache, PresenceOptions options) : IPresenceStore
{
    private long _cacheFailures;

    /// <summary>How many cache reads or writes failed since start. For diagnostics and tests.</summary>
    public long CacheFailures => Interlocked.Read(ref _cacheFailures);

    /// <inheritdoc />
    public async ValueTask<bool> UpsertAsync(PresenceRow row, CancellationToken ct)
    {
        var stored = await inner.UpsertAsync(row, ct).ConfigureAwait(false);
        if (stored)
        {
            try
            {
                var bytes = JsonSerializer.SerializeToUtf8Bytes(row, PresenceCacheJson.Default.PresenceRow);
                await cache.SetAsync(Key(row.AgentId), bytes, options.CacheTtl, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Interlocked.Increment(ref _cacheFailures);
            }
        }

        return stored;
    }

    /// <inheritdoc />
    public async ValueTask<PresenceRow?> GetAsync(Guid agentId, CancellationToken ct)
    {
        var row = await inner.GetAsync(agentId, ct).ConfigureAwait(false);
        if (row is not null)
        {
            return row;
        }

        try
        {
            var bytes = await cache.GetAsync(Key(agentId), ct).ConfigureAwait(false);
            return bytes is { } b ? JsonSerializer.Deserialize(b.Span, PresenceCacheJson.Default.PresenceRow) : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Interlocked.Increment(ref _cacheFailures);
            return null;
        }
    }

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<PresenceRow>> GetAllAsync(CancellationToken ct) => inner.GetAllAsync(ct);

    private string Key(Guid agentId) => options.CacheKeyPrefix + agentId.ToString("N");
}
