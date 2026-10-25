namespace Bazaar.Server.Providers;

/// <summary>Cache and presence only. GC fan-out is Postgres NOTIFY (doc 04, C07), and the platform works with the cache down.</summary>
public interface ICacheProvider : IProvider
{
    Task SetAsync(string key, ReadOnlyMemory<byte> value, TimeSpan ttl, CancellationToken ct);

    Task<ReadOnlyMemory<byte>?> GetAsync(string key, CancellationToken ct);
}
