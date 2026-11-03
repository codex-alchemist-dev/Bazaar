using Bazaar.Server.Providers;
using Bazaar.Server.Providers.Linux;
using Bazaar.Server.Secrets;

namespace Bazaar.Server.Tests.Providers.Linux;

public class ValkeyCacheProviderTests
{
    private const string _endpoint = "cache.internal:6379";
    private const string _secretReference = "env:BAZAAR_TEST_VALKEY";

    [Fact]
    public void Probe_is_usable_with_an_endpoint_and_reports_ttl()
    {
        var client = new FakeValkeyClient();
        var provider = Create(new ValkeyCacheOptions { Endpoint = _endpoint }, client: client);

        var status = provider.Probe();

        Assert.True(status.Usable);
        Assert.Null(status.Reason);
        Assert.Equal("valkey", status.Kind);
        Assert.Equal(["ttl"], status.Features);
        Assert.Equal(1, client.PingCount);
        Assert.Equal(_endpoint, Assert.Single(FakeValkeyClientFactory.Requested));
    }

    [Fact]
    public void Probe_reports_unreachable_server_as_status_without_leaking_the_endpoint()
    {
        var client = new FakeValkeyClient { PingFailure = new InvalidOperationException($"no route to {_endpoint}") };
        var provider = Create(new ValkeyCacheOptions { Endpoint = _endpoint }, client: client);

        var status = provider.Probe();

        Assert.False(status.Usable);
        Assert.Empty(status.Features);
        Assert.Equal("connection failed (InvalidOperationException)", status.Reason);
        Assert.DoesNotContain(_endpoint, status.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Probe_reports_a_server_that_does_not_answer_within_the_timeout()
    {
        var client = new FakeValkeyClient { PingHangs = true };
        var provider = Create(new ValkeyCacheOptions { Endpoint = _endpoint, ProbeTimeout = TimeSpan.FromMilliseconds(50) }, client: client);

        var status = provider.Probe();

        Assert.False(status.Usable);
        Assert.Equal("no answer within 0.05 seconds", status.Reason);
    }

    [Fact]
    public void Probe_resolves_the_secret_reference_and_connects_with_the_value_not_the_reference()
    {
        var resolver = new FakeSecretResolver(new Dictionary<string, string> { [_secretReference] = "valkey.internal:6380,ssl=true" });
        var client = new FakeValkeyClient();
        var provider = Create(new ValkeyCacheOptions { SecretReference = _secretReference }, resolver, client);

        var status = provider.Probe();

        Assert.True(status.Usable);
        Assert.Equal("valkey.internal:6380,ssl=true", Assert.Single(FakeValkeyClientFactory.Requested));
    }

    [Fact]
    public void Probe_reports_a_failed_secret_resolution_without_throwing()
    {
        var resolver = new FakeSecretResolver(new Dictionary<string, string>());
        var provider = Create(new ValkeyCacheOptions { SecretReference = _secretReference }, resolver);

        var status = provider.Probe();

        Assert.False(status.Usable);
        Assert.Contains(_secretReference, status.Reason, StringComparison.Ordinal);
        Assert.Empty(FakeValkeyClientFactory.Requested);
    }

    [Fact]
    public void Probe_reports_a_secret_reference_when_no_resolver_is_available()
    {
        var provider = Create(new ValkeyCacheOptions { SecretReference = _secretReference }, resolver: null);

        var status = provider.Probe();

        Assert.False(status.Usable);
        Assert.Equal("the secret reference uses a scheme this machine cannot resolve", status.Reason);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", "  ")]
    [InlineData("cache.internal:6379", _secretReference)]
    public void Probe_requires_exactly_one_of_endpoint_or_secret_reference(string? endpoint, string? secretReference)
    {
        var client = new FakeValkeyClient();
        var provider = Create(new ValkeyCacheOptions { Endpoint = endpoint, SecretReference = secretReference }, client: client);

        var status = provider.Probe();

        Assert.False(status.Usable);
        Assert.Equal("set exactly one of Endpoint or SecretReference", status.Reason);
        Assert.Empty(FakeValkeyClientFactory.Requested);
    }

    [Fact]
    public void Probe_rejects_a_non_positive_timeout_as_a_status()
    {
        var provider = Create(new ValkeyCacheOptions { Endpoint = _endpoint, ProbeTimeout = TimeSpan.Zero });

        var status = provider.Probe();

        Assert.False(status.Usable);
        Assert.Equal("ProbeTimeout must be positive", status.Reason);
    }

    [Fact]
    public void Probe_reports_a_factory_that_throws_as_status()
    {
        var provider = new ValkeyCacheProvider(
            new ValkeyCacheOptions { Endpoint = _endpoint }, null, new ThrowingFactory());

        var status = provider.Probe();

        Assert.False(status.Usable);
        Assert.Equal("connection failed (FormatException)", status.Reason);
    }

    [Fact]
    public async Task Set_then_get_round_trips_bytes_under_the_key_prefix()
    {
        var client = new FakeValkeyClient();
        var provider = Create(new ValkeyCacheOptions { Endpoint = _endpoint, KeyPrefix = "test:" }, client: client);

        await provider.SetAsync("presence/ada", new byte[] { 1, 2, 3 }, TimeSpan.FromMinutes(5), TestContext.Current.CancellationToken);
        var value = await provider.GetAsync("presence/ada", TestContext.Current.CancellationToken);

        Assert.Equal(new byte[] { 1, 2, 3 }, value!.Value.ToArray());
        Assert.Contains("test:presence/ada", client.Store.Keys);
        Assert.DoesNotContain("presence/ada", client.Store.Keys);
    }

    [Fact]
    public async Task Set_passes_the_time_to_live_to_the_client()
    {
        var client = new FakeValkeyClient();
        var provider = Create(new ValkeyCacheOptions { Endpoint = _endpoint }, client: client);

        await provider.SetAsync("k", new byte[] { 9 }, TimeSpan.FromSeconds(90), TestContext.Current.CancellationToken);

        Assert.Equal(TimeSpan.FromSeconds(90), client.LastTtl);
    }

    [Fact]
    public async Task Set_rejects_a_zero_time_to_live()
    {
        var provider = Create(new ValkeyCacheOptions { Endpoint = _endpoint }, client: new FakeValkeyClient());

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            provider.SetAsync("k", new byte[] { 1 }, TimeSpan.Zero, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Get_of_a_missing_key_returns_null()
    {
        var provider = Create(new ValkeyCacheOptions { Endpoint = _endpoint }, client: new FakeValkeyClient());

        Assert.Null(await provider.GetAsync("absent", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Delete_removes_the_prefixed_key()
    {
        var client = new FakeValkeyClient();
        var provider = Create(new ValkeyCacheOptions { Endpoint = _endpoint }, client: client);
        await provider.SetAsync("k", new byte[] { 1 }, TimeSpan.FromMinutes(1), TestContext.Current.CancellationToken);

        await provider.DeleteAsync("k", TestContext.Current.CancellationToken);

        Assert.Null(await provider.GetAsync("k", TestContext.Current.CancellationToken));
        Assert.Empty(client.Store);
    }

    [Fact]
    public async Task Cache_calls_degrade_when_the_server_is_down()
    {
        var client = new FakeValkeyClient { OperationFailure = new IOException("connection reset") };
        var provider = Create(new ValkeyCacheOptions { Endpoint = _endpoint }, client: client);
        var ct = TestContext.Current.CancellationToken;

        await provider.SetAsync("k", new byte[] { 1 }, TimeSpan.FromMinutes(1), ct);
        await provider.DeleteAsync("k", ct);
        var value = await provider.GetAsync("k", ct);

        Assert.Null(value);
        Assert.Empty(client.Store);
    }

    [Fact]
    public async Task Caller_cancellation_is_not_swallowed_by_a_cache_call()
    {
        var provider = Create(new ValkeyCacheOptions { Endpoint = _endpoint }, client: new FakeValkeyClient());
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.GetAsync("k", cancelled.Token));
    }

    [Fact]
    public async Task One_client_serves_every_call()
    {
        var client = new FakeValkeyClient();
        var provider = Create(new ValkeyCacheOptions { Endpoint = _endpoint }, client: client);
        var ct = TestContext.Current.CancellationToken;

        provider.Probe();
        await provider.SetAsync("k", new byte[] { 1 }, TimeSpan.FromMinutes(1), ct);
        await provider.GetAsync("k", ct);

        Assert.Single(FakeValkeyClientFactory.Requested);
    }

    [Fact]
    public void Cache_candidates_offer_valkey_on_linux_and_nothing_elsewhere()
    {
        var candidates = LinuxProviders.CacheCandidates(new ValkeyCacheOptions { Endpoint = _endpoint });

        if (OperatingSystem.IsLinux())
        {
            var candidate = Assert.Single(candidates);
            Assert.IsType<ValkeyCacheProvider>(candidate);
            Assert.Equal("valkey", candidate.Kind);
        }
        else
        {
            Assert.Empty(candidates);
        }
    }

    [Fact]
    public void Cache_auto_selection_is_none_when_the_valkey_server_is_unreachable()
    {
        var candidates = new ICacheProvider[] { Create(new ValkeyCacheOptions { Endpoint = _endpoint }, client: new FakeValkeyClient { PingFailure = new IOException("refused") }) };

        var selection = ProviderSelector.Select(ProviderRole.Cache, ProviderSelector.Auto, candidates);

        Assert.Null(selection.Provider);
        Assert.False(Assert.Single(selection.Statuses).Usable);
    }

    private static ValkeyCacheProvider Create(
        ValkeyCacheOptions options,
        ISecretResolver? resolver = null,
        FakeValkeyClient? client = null)
    {
        var factory = client is null ? new FakeValkeyClientFactory() : new FakeValkeyClientFactory(client);
        return new ValkeyCacheProvider(options, resolver, factory);
    }

    private sealed class FakeValkeyClient : IValkeyClient
    {
        public Exception? PingFailure { get; init; }

        public Exception? OperationFailure { get; init; }

        public bool PingHangs { get; init; }

        public int PingCount { get; private set; }

        public TimeSpan LastTtl { get; private set; }

        public Dictionary<string, byte[]> Store { get; } = new(StringComparer.Ordinal);

        public async Task PingAsync(CancellationToken ct)
        {
            PingCount++;
            if (PingHangs)
            {
                await Task.Delay(Timeout.Infinite, ct);
            }

            if (PingFailure is not null)
            {
                throw PingFailure;
            }
        }

        public Task<byte[]?> GetAsync(string key, CancellationToken ct)
        {
            ThrowIfFailing();
            return Task.FromResult(Store.TryGetValue(key, out var value) ? value : null);
        }

        public Task SetAsync(string key, byte[] value, TimeSpan ttl, CancellationToken ct)
        {
            ThrowIfFailing();
            LastTtl = ttl;
            Store[key] = value;
            return Task.CompletedTask;
        }

        public Task DeleteAsync(string key, CancellationToken ct)
        {
            ThrowIfFailing();
            Store.Remove(key);
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private void ThrowIfFailing()
        {
            if (OperationFailure is not null)
            {
                throw OperationFailure;
            }
        }
    }

    private sealed class FakeValkeyClientFactory : IValkeyClientFactory
    {
        private readonly FakeValkeyClient _client;

        public FakeValkeyClientFactory()
            : this(new FakeValkeyClient())
        {
        }

        public FakeValkeyClientFactory(FakeValkeyClient client)
        {
            _client = client;
            Requested.Clear();
        }

        public static List<string> Requested { get; } = [];

        public IValkeyClient Create(string connectionString)
        {
            Requested.Add(connectionString);
            return _client;
        }
    }

    private sealed class ThrowingFactory : IValkeyClientFactory
    {
        public IValkeyClient Create(string connectionString) => throw new FormatException("bad connection string");
    }

    private sealed class FakeSecretResolver(IReadOnlyDictionary<string, string> values) : ISecretResolver
    {
        public bool CanResolve(string reference) => reference.StartsWith("env:", StringComparison.Ordinal);

        public ValueTask<string> ResolveAsync(string reference, CancellationToken cancellationToken) =>
            values.TryGetValue(reference, out var value)
                ? ValueTask.FromResult(value)
                : ValueTask.FromException<string>(new SecretResolutionException(reference, "not set"));
    }
}
