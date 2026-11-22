using Bazaar.Server.Providers;
using Bazaar.Server.Secrets;

namespace Bazaar.Server.Valkey;

/// <summary>
/// The Linux cache and presence provider on Valkey (doc 03 WP12, C07). Cache only: GC fan-out stays on Postgres NOTIFY, and the
/// platform keeps working with this provider down.
/// <para>
/// <see cref="Probe"/> never throws. A missing or invalid setting, an unresolved secret, an unreachable server or a server that does
/// not answer within <see cref="ValkeyCacheOptions.ProbeTimeout"/> all come back as an unusable <see cref="ProviderStatus"/> with a reason
/// that names no secret. Runtime calls degrade instead of throwing: a failed read is a miss, and a failed write or delete is dropped.
/// Callers must not treat the cache as durable.
/// </para>
/// </summary>
public sealed class ValkeyCacheProvider : ICacheProvider, ICacheCounter, IAsyncDisposable
{
    /// <summary>The kind name this provider reports.</summary>
    public const string KindName = "valkey";

    private const string _ttlFeature = "ttl";

    /// <summary>Pause between readiness checks inside the start window.</summary>
    private static readonly TimeSpan _startPollInterval = TimeSpan.FromMilliseconds(50);

    private readonly ValkeyCacheOptions _options;
    private readonly ISecretResolver? _secretResolver;
    private readonly IValkeyClientFactory _clientFactory;
    private readonly IValkeyConnectionSource? _managed;
    private readonly ValkeyConfiguredEndpoint _endpoint;
    private readonly SemaphoreSlim _clientGate = new(1, 1);
    private IValkeyClient? _client;
    private string? _clientConnectionString;
    private int _startWindowPending = 1;

    /// <summary>Creates the provider. Nothing connects until the first probe or cache call.</summary>
    /// <param name="options">Endpoint or secret reference (unmanaged), key prefix and probe timeout.</param>
    /// <param name="secretResolver">Resolves <see cref="ValkeyCacheOptions.SecretReference"/>. Null when no secret scheme is available.</param>
    /// <param name="clientFactory">Creates the client; tests pass a fake.</param>
    /// <param name="managed">
    /// The managed child's connection (doc 40 section 4c.1). When set, the cache takes no endpoint or secret, and every call asks for the
    /// current connection, so a child on a new port or a restarted child is reached without a host restart.
    /// </param>
    public ValkeyCacheProvider(
        ValkeyCacheOptions options,
        ISecretResolver? secretResolver,
        IValkeyClientFactory clientFactory,
        IValkeyConnectionSource? managed = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(clientFactory);
        _options = options;
        _secretResolver = secretResolver;
        _clientFactory = clientFactory;
        _managed = managed;
        _endpoint = new ValkeyConfiguredEndpoint(options, secretResolver);
    }

    /// <inheritdoc />
    public string Kind => KindName;

    /// <summary>True when the cache uses the host's managed child rather than an operator endpoint.</summary>
    public bool IsManaged => _managed is not null;

    /// <inheritdoc />
    public ProviderStatus Probe()
    {
        if (ConfigurationError() is { } configurationError)
        {
            return Unusable(configurationError);
        }

        // Only the first probe waits for a managed child to start (doc 40 section 4c.4 item 8). Later probes answer at once, so a status
        // request is never held for the window.
        if (_managed is not null && Interlocked.Exchange(ref _startWindowPending, 0) == 1
            && !WaitForManagedChild().GetAwaiter().GetResult())
        {
            return Unusable($"managed server did not answer within {_options.StartWindowSeconds} seconds of start");
        }

        try
        {
            using var timeout = new CancellationTokenSource(_options.ProbeTimeout);
            PingAsync(timeout.Token).GetAwaiter().GetResult();
            return new ProviderStatus(Kind, true, [_ttlFeature], null);
        }
        catch (OperationCanceledException)
        {
            return Unusable($"no answer within {_options.ProbeTimeout.TotalSeconds:0.###} seconds");
        }
        catch (SecretResolutionException ex)
        {
            return Unusable(ex.Message);
        }
        catch (Exception ex)
        {
            // The type name only: connection exception messages can echo the endpoint.
            return Unusable($"connection failed ({ex.GetType().Name})");
        }
    }

    /// <inheritdoc />
    public async Task SetAsync(string key, ReadOnlyMemory<byte> value, TimeSpan ttl, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(ttl, TimeSpan.Zero);

        try
        {
            var client = await ClientAsync(ct);
            await client.SetAsync(Prefixed(key), value.ToArray(), ttl, ct);
        }
        catch (Exception ex) when (IsDegradable(ex, ct))
        {
            // Degraded: the write is dropped and the next probe reports the server state.
        }
    }

    /// <inheritdoc />
    public async Task<ReadOnlyMemory<byte>?> GetAsync(string key, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        try
        {
            var client = await ClientAsync(ct);
            byte[]? value = await client.GetAsync(Prefixed(key), ct);
            return value is null ? null : (ReadOnlyMemory<byte>?)new ReadOnlyMemory<byte>(value);
        }
        catch (Exception ex) when (IsDegradable(ex, ct))
        {
            // Degraded: a failed read is reported as a miss.
            return null;
        }
    }

    /// <inheritdoc />
    public async Task DeleteAsync(string key, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        try
        {
            var client = await ClientAsync(ct);
            await client.DeleteAsync(Prefixed(key), ct);
        }
        catch (Exception ex) when (IsDegradable(ex, ct))
        {
            // Degraded: the delete is dropped; entries still expire by TTL.
        }
    }

    /// <summary>
    /// Atomic counter for the shared rate limiter (<see cref="ICacheCounter"/>). Unlike the cache calls this one throws on any failure,
    /// so the caller sees an outage and falls back. The key is prefixed like every other cache key.
    /// </summary>
    public async Task<long> IncrementAsync(string key, long by, TimeSpan ttl, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(ttl, TimeSpan.Zero);

        var client = await ClientAsync(ct);
        return await client.IncrementAsync(Prefixed(key), by, ttl, ct);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_client is not null)
        {
            await _client.DisposeAsync();
        }

        _clientGate.Dispose();
    }

    private static ProviderStatus Unusable(string reason) => new(KindName, false, [], reason);

    private static bool IsDegradable(Exception ex, CancellationToken ct) =>
        !(ex is OperationCanceledException && ct.IsCancellationRequested);

    private string Prefixed(string key) => _options.KeyPrefix + key;

    /// <summary>Returns the reason the settings cannot produce a connection, or null when they can.</summary>
    private string? ConfigurationError()
    {
        var hasEndpoint = !string.IsNullOrWhiteSpace(_options.Endpoint);
        var hasSecret = !string.IsNullOrWhiteSpace(_options.SecretReference);

        if (_managed is not null)
        {
            if (hasEndpoint || hasSecret)
            {
                return "a managed cache takes no endpoint or secret reference";
            }
        }
        else
        {
            if (hasEndpoint == hasSecret)
            {
                return "set exactly one of Endpoint or SecretReference";
            }

            if (hasSecret && _secretResolver?.CanResolve(_options.SecretReference!) != true)
            {
                return "the secret reference uses a scheme this machine cannot resolve";
            }
        }

        if (_options.ProbeTimeout <= TimeSpan.Zero)
        {
            return "ProbeTimeout must be positive";
        }

        if (_options.StartWindowSeconds is < 1 or > 120)
        {
            return "StartWindowSeconds must be between 1 and 120";
        }

        return null;
    }

    /// <summary>
    /// Retries the managed connection until the child is ready or the start window closes. Returns false when the window closes first.
    /// A failed attempt is only "not ready yet", so it never throws out of here.
    /// </summary>
    private async Task<bool> WaitForManagedChild()
    {
        using var window = new CancellationTokenSource(TimeSpan.FromSeconds(_options.StartWindowSeconds));
        while (true)
        {
            try
            {
                // The source does not observe the window token: the check is a state read, not a network call.
                await _managed!.GetConnectionStringAsync(CancellationToken.None);
                return true;
            }
            catch (Exception)
            {
                // Not ready yet: the child has not answered its readiness ping.
            }

            try
            {
                await Task.Delay(_startPollInterval, window.Token);
            }
            catch (OperationCanceledException)
            {
                return false;
            }
        }
    }

    private async Task PingAsync(CancellationToken ct)
    {
        var client = await ClientAsync(ct);
        await client.PingAsync(ct);
    }

    private async Task<IValkeyClient> ClientAsync(CancellationToken ct)
    {
        if (_managed is not null)
        {
            return await ManagedClientAsync(ct);
        }

        if (_client is not null)
        {
            return _client;
        }

        await _clientGate.WaitAsync(ct);
        try
        {
            if (_client is null)
            {
                if (ConfigurationError() is { } error)
                {
                    throw new InvalidOperationException($"Valkey cache is not configured: {error}.");
                }

                var connectionString = await _endpoint.GetConnectionStringAsync(ct);
                _client = _clientFactory.Create(connectionString);
            }

            return _client;
        }
        finally
        {
            _clientGate.Release();
        }
    }

    /// <summary>
    /// The managed path asks for the current connection on every call. The client is replaced when the connection changes (a new port, or
    /// a child that was restarted with a new address), so a stale address is never reused. While the child is down the call throws and the
    /// caller degrades.
    /// </summary>
    private async Task<IValkeyClient> ManagedClientAsync(CancellationToken ct)
    {
        if (ConfigurationError() is { } error)
        {
            throw new InvalidOperationException($"Valkey cache is not configured: {error}.");
        }

        var connectionString = await _managed!.GetConnectionStringAsync(ct);
        await _clientGate.WaitAsync(ct);
        try
        {
            if (_client is null || _clientConnectionString != connectionString)
            {
                if (_client is not null)
                {
                    await _client.DisposeAsync();
                }

                _client = _clientFactory.Create(connectionString);
                _clientConnectionString = connectionString;
            }

            return _client;
        }
        finally
        {
            _clientGate.Release();
        }
    }
}
