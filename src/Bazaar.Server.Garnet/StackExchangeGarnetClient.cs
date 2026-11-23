using System.Globalization;
using Bazaar.Server.Valkey;
using StackExchange.Redis;

namespace Bazaar.Server.Garnet;

/// <summary>
/// Creates <see cref="StackExchangeGarnetClient"/> instances. StackExchange.Redis speaks the Redis protocol that Garnet implements.
/// </summary>
public sealed class StackExchangeGarnetClientFactory : IValkeyClientFactory
{
    /// <inheritdoc />
    public IValkeyClient Create(string connectionString) => new StackExchangeGarnetClient(connectionString);
}

/// <summary>
/// The StackExchange.Redis adapter behind <see cref="IValkeyClient"/>. The multiplexer connects on first use and keeps retrying in the
/// background (AbortOnConnectFail is off), so a Garnet server that starts later is picked up without a restart.
/// </summary>
public sealed class StackExchangeGarnetClient : IValkeyClient
{
    private readonly ConfigurationOptions _options;
    private readonly Lock _gate = new();
    private Task<ConnectionMultiplexer>? _connecting;

    /// <summary>Parses the connection string. Throws <see cref="ArgumentException"/> when it is malformed.</summary>
    public StackExchangeGarnetClient(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        _options = ConfigurationOptions.Parse(connectionString);
        _options.AbortOnConnectFail = false;
        // CONFIG GET (the eviction check) needs admin mode in StackExchange.Redis. The client issues only read commands of that kind.
        _options.AllowAdmin = true;
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
    public async Task<long> IncrementAsync(string key, long by, TimeSpan ttl, CancellationToken ct)
    {
        var database = await DatabaseAsync(ct);

        // One MULTI/EXEC: the increment and "EXPIRE NX" (only when the key has no expiry) apply together or not at all.
        var transaction = database.CreateTransaction();
        var increment = transaction.StringIncrementAsync(key, by);
        _ = transaction.KeyExpireAsync(key, ttl, ExpireWhen.HasNoExpiry);
        if (!await transaction.ExecuteAsync().WaitAsync(ct))
        {
            throw new InvalidOperationException("The counter transaction was not committed.");
        }

        return await increment;
    }

    /// <inheritdoc />
    public async Task<ValkeyMemoryInfo> MemoryInfoAsync(CancellationToken ct)
    {
        var database = await DatabaseAsync(ct);
        try
        {
            RedisResult info = await database.ExecuteAsync("INFO", "memory").WaitAsync(ct);
            var text = (string?)info ?? string.Empty;
            return new ValkeyMemoryInfo(InfoValue(text, "used_memory"), InfoValue(text, "maxmemory"));
        }
        catch (RedisServerException)
        {
            // Garnet does not implement every INFO section or field. Missing data reads as zero (unknown), not as a failure.
            return new ValkeyMemoryInfo(0, 0);
        }
    }

    /// <inheritdoc />
    public async Task<string?> GetConfigAsync(string parameter, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(parameter);
        var database = await DatabaseAsync(ct);
        try
        {
            RedisResult reply = await database.ExecuteAsync("CONFIG", "GET", parameter).WaitAsync(ct);
            var pairs = (RedisResult[]?)reply;
            return pairs is { Length: 2 } ? (string?)pairs[1] : null;
        }
        catch (RedisServerException)
        {
            // Garnet supports only a subset of CONFIG GET; an unsupported parameter is "not reported".
            return null;
        }
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

    /// <summary>Reads one numeric field of an INFO section. Returns 0 when the field is missing or not a number.</summary>
    internal static long InfoValue(string text, string field)
    {
        foreach (var line in text.Split('\n'))
        {
            var separator = line.IndexOf(':', StringComparison.Ordinal);
            if (separator > 0
                && string.Equals(line[..separator], field, StringComparison.Ordinal)
                && long.TryParse(line.AsSpan(separator + 1).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
            {
                return value;
            }
        }

        return 0;
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
