using StackExchange.Redis;

namespace Bazaar.Server.Providers.Linux;

/// <summary>
/// Creates <see cref="StackExchangeValkeyClient"/> instances. StackExchange.Redis speaks the Redis protocol that Valkey implements.
/// </summary>
public sealed class StackExchangeValkeyClientFactory : IValkeyClientFactory
{
    /// <inheritdoc />
    public IValkeyClient Create(string connectionString) => new StackExchangeValkeyClient(connectionString);
}

/// <summary>
/// The StackExchange.Redis adapter behind <see cref="IValkeyClient"/>. The multiplexer connects on first use and keeps retrying in the
/// background (AbortOnConnectFail is off), so a Valkey server that starts later is picked up without a restart.
/// </summary>
public sealed class StackExchangeValkeyClient : IValkeyClient
{
    private readonly ConfigurationOptions _options;
    private readonly Lock _gate = new();
    private Task<ConnectionMultiplexer>? _connecting;

    /// <summary>Parses the connection string. Throws <see cref="ArgumentException"/> when it is malformed.</summary>
    public StackExchangeValkeyClient(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        _options = ConfigurationOptions.Parse(connectionString);
        _options.AbortOnConnectFail = false;
        _options.ClientName ??= "bazaar-server";
    }

    /// <inheritdoc />
    public async Task PingAsync(CancellationToken ct)
    {
        var database = await DatabaseAsync(ct);
        await database.PingAsync().WaitAsync(ct);
    }

    /// <inheritdoc />
    public async Task<byte[]?> GetAsync(string key, CancellationToken ct)
    {
        var database = await DatabaseAsync(ct);
        RedisValue value = await database.StringGetAsync(key).WaitAsync(ct);
        return value.IsNull ? null : (byte[]?)value;
    }

    /// <inheritdoc />
    public async Task SetAsync(string key, byte[] value, TimeSpan ttl, CancellationToken ct)
    {
        var database = await DatabaseAsync(ct);
        await database.StringSetAsync(key, (RedisValue)value, ttl).WaitAsync(ct);
    }

    /// <inheritdoc />
    public async Task DeleteAsync(string key, CancellationToken ct)
    {
        var database = await DatabaseAsync(ct);
        await database.KeyDeleteAsync(key).WaitAsync(ct);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        Task<ConnectionMultiplexer>? connecting;
        lock (_gate)
        {
            connecting = _connecting;
            _connecting = null;
        }

        if (connecting is { IsCompletedSuccessfully: true })
        {
            await connecting.Result.DisposeAsync();
        }
    }

    private async Task<IDatabase> DatabaseAsync(CancellationToken ct)
    {
        Task<ConnectionMultiplexer> connecting;
        lock (_gate)
        {
            _connecting ??= ConnectionMultiplexer.ConnectAsync(_options);
            connecting = _connecting;
        }

        try
        {
            var multiplexer = await connecting.WaitAsync(ct);
            return multiplexer.GetDatabase();
        }
        catch (Exception) when (connecting.IsFaulted)
        {
            // A failed connect is not cached, so the next operation tries again.
            lock (_gate)
            {
                if (ReferenceEquals(_connecting, connecting))
                {
                    _connecting = null;
                }
            }

            throw;
        }
    }
}
