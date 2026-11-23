namespace Bazaar.Server.Valkey.Tests;

/// <summary>An in-memory Valkey client. Failures, memory and configuration values are set by the test.</summary>
internal sealed class TestValkeyClient : IValkeyClient
{
    public Exception? Failure { get; set; }

    public bool ReturnsOtherBytes { get; set; }

    public ValkeyMemoryInfo Memory { get; set; } = new(0, 0);

    public Dictionary<string, string?> Config { get; } = new(StringComparer.Ordinal);

    public Dictionary<string, byte[]> Store { get; } = new(StringComparer.Ordinal);

    public bool Disposed { get; private set; }

    public Task PingAsync(CancellationToken ct)
    {
        ThrowIfFailing();
        return Task.CompletedTask;
    }

    public Task<byte[]?> GetAsync(string key, CancellationToken ct)
    {
        ThrowIfFailing();
        if (!Store.TryGetValue(key, out var value))
        {
            return Task.FromResult<byte[]?>(null);
        }

        return Task.FromResult<byte[]?>(ReturnsOtherBytes ? [0xFF] : value);
    }

    public Task SetAsync(string key, byte[] value, TimeSpan ttl, CancellationToken ct)
    {
        ThrowIfFailing();
        Store[key] = value;
        return Task.CompletedTask;
    }

    public Task DeleteAsync(string key, CancellationToken ct)
    {
        ThrowIfFailing();
        Store.Remove(key);
        return Task.CompletedTask;
    }

    public Task<ValkeyMemoryInfo> MemoryInfoAsync(CancellationToken ct)
    {
        ThrowIfFailing();
        return Task.FromResult(Memory);
    }

    public Dictionary<string, long> Counters { get; } = new(StringComparer.Ordinal);

    public Task<long> IncrementAsync(string key, long by, TimeSpan ttl, CancellationToken ct)
    {
        ThrowIfFailing();
        Counters[key] = Counters.GetValueOrDefault(key) + by;
        return Task.FromResult(Counters[key]);
    }

    public Task<string?> GetConfigAsync(string parameter, CancellationToken ct)
    {
        ThrowIfFailing();
        return Task.FromResult(Config.GetValueOrDefault(parameter));
    }

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        return ValueTask.CompletedTask;
    }

    private void ThrowIfFailing()
    {
        if (Failure is not null)
        {
            throw Failure;
        }
    }
}

/// <summary>Creates <see cref="TestValkeyClient"/>s and records every connection string it was asked for.</summary>
internal sealed class TestClientFactory : IValkeyClientFactory
{
    public List<string> ConnectionStrings { get; } = [];

    public List<TestValkeyClient> Clients { get; } = [];

    /// <summary>Applied to each new client, so a test can configure what the next probe sees.</summary>
    public Action<TestValkeyClient>? Configure { get; init; }

    public IValkeyClient Create(string connectionString)
    {
        ConnectionStrings.Add(connectionString);
        var client = new TestValkeyClient();
        Configure?.Invoke(client);
        Clients.Add(client);
        return client;
    }
}

/// <summary>A connection source the test moves: set a value to make a connection available, null to make it unavailable.</summary>
internal sealed class TestConnectionSource : IValkeyConnectionSource
{
    public string? Value { get; set; }

    public ValueTask<string> GetConnectionStringAsync(CancellationToken ct) =>
        Value is null
            ? ValueTask.FromException<string>(new InvalidOperationException("The managed Valkey server is not running."))
            : ValueTask.FromResult(Value);
}
