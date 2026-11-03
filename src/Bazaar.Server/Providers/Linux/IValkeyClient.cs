namespace Bazaar.Server.Providers.Linux;

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
}

/// <summary>Creates a client for a resolved connection string. Tests inject a fake factory.</summary>
public interface IValkeyClientFactory
{
    /// <summary>Creates a client. It must not connect synchronously; the first operation connects.</summary>
    IValkeyClient Create(string connectionString);
}
