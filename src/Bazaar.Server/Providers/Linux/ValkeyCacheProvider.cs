using Bazaar.Server.Secrets;

namespace Bazaar.Server.Providers.Linux;

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
public sealed class ValkeyCacheProvider : ICacheProvider, IAsyncDisposable
{
    /// <summary>The kind name <c>host.provider.cache</c> selects.</summary>
    public const string KindName = "valkey";

    private const string _ttlFeature = "ttl";

    private readonly ValkeyCacheOptions _options;
    private readonly ISecretResolver? _secretResolver;
    private readonly IValkeyClientFactory _clientFactory;
    private readonly SemaphoreSlim _clientGate = new(1, 1);
    private IValkeyClient? _client;

    /// <summary>Creates the provider. Nothing connects until the first probe or cache call.</summary>
    /// <param name="options">Endpoint or secret reference, key prefix and probe timeout.</param>
    /// <param name="secretResolver">Resolves <see cref="ValkeyCacheOptions.SecretReference"/>. Null when no secret scheme is available.</param>
    /// <param name="clientFactory">Creates the client; tests pass a fake.</param>
    public ValkeyCacheProvider(ValkeyCacheOptions options, ISecretResolver? secretResolver, IValkeyClientFactory clientFactory)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(clientFactory);
        _options = options;
        _secretResolver = secretResolver;
        _clientFactory = clientFactory;
    }

    /// <inheritdoc />
    public string Kind => KindName;

    /// <inheritdoc />
    public ProviderStatus Probe()
    {
        if (ConfigurationError() is { } configurationError)
        {
            return Unusable(configurationError);
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

        if (hasEndpoint == hasSecret)
        {
            return "set exactly one of Endpoint or SecretReference";
        }

        if (hasSecret && _secretResolver?.CanResolve(_options.SecretReference!) != true)
        {
            return "the secret reference uses a scheme this machine cannot resolve";
        }

        if (_options.ProbeTimeout <= TimeSpan.Zero)
        {
            return "ProbeTimeout must be positive";
        }

        return null;
    }

    private async Task PingAsync(CancellationToken ct)
    {
        var client = await ClientAsync(ct);
        await client.PingAsync(ct);
    }

    private async Task<IValkeyClient> ClientAsync(CancellationToken ct)
    {
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

                var connectionString = await ResolveConnectionStringAsync(ct);
                _client = _clientFactory.Create(connectionString);
            }

            return _client;
        }
        finally
        {
            _clientGate.Release();
        }
    }

    private async Task<string> ResolveConnectionStringAsync(CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(_options.Endpoint))
        {
            return _options.Endpoint.Trim();
        }

        return await _secretResolver!.ResolveAsync(_options.SecretReference!, ct);
    }
}
