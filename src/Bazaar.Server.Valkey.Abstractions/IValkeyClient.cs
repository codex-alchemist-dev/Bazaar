namespace Bazaar.Server.Valkey;

/// <summary>
/// The narrow surface the Valkey provider needs from a client library. The provider and its tests depend on this interface only;
/// <see cref="StackExchangeValkeyClient"/> is the one implementation that touches the library.
/// Implementations throw when the server cannot be reached; the provider turns that into a status or a degraded cache call.
/// </summary>
public interface IValkeyClient : IAsyncDisposable
{
    /// <summary>Round-trips a PING to the server.</summary>
    Task PingAsync(CancellationToken ct);

    /// <summary>Returns the stored bytes, or null when the key is missing or expired.</summary>
    Task<byte[]?> GetAsync(string key, CancellationToken ct);

    /// <summary>Stores the bytes with a time to live. The server expires the key on its own.</summary>
    Task SetAsync(string key, byte[] value, TimeSpan ttl, CancellationToken ct);

    /// <summary>Removes the key. Returns without error when it is missing.</summary>
    Task DeleteAsync(string key, CancellationToken ct);

    /// <summary>
    /// Atomically adds <paramref name="by"/> to the integer at <paramref name="key"/> (a missing key counts as zero) and returns the new value.
    /// The expiry is set only when the key has none, in the same atomic step. Throws when the server cannot be reached.
    /// </summary>
    Task<long> IncrementAsync(string key, long by, TimeSpan ttl, CancellationToken ct);

    /// <summary>Reads the memory section of the server (used and configured maximum bytes). Used by the health probes.</summary>
    Task<ValkeyMemoryInfo> MemoryInfoAsync(CancellationToken ct);

    /// <summary>Reads one server configuration value (<c>CONFIG GET</c>), or null when the server does not report it.</summary>
    Task<string?> GetConfigAsync(string parameter, CancellationToken ct);
}

/// <summary>Memory use reported by the server. <see cref="MaxBytes"/> is 0 when the server has no memory limit.</summary>
/// <param name="UsedBytes">Bytes the server currently uses for data.</param>
/// <param name="MaxBytes">The configured <c>maxmemory</c>, or 0 for no limit.</param>
public sealed record ValkeyMemoryInfo(long UsedBytes, long MaxBytes);

/// <summary>Creates a client for a resolved connection string. Tests inject a fake factory.</summary>
public interface IValkeyClientFactory
{
    /// <summary>Creates a client. It must not connect synchronously; the first operation connects.</summary>
    IValkeyClient Create(string connectionString);
}
