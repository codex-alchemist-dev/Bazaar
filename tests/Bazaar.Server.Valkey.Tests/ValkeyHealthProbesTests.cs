namespace Bazaar.Server.Valkey.Tests;

/// <summary>The four Kodo probes (doc 42 section 11). They report results and never throw on a failed server call.</summary>
public class ValkeyHealthProbesTests
{
    private const string _endpoint = "cache.internal:6379";

    [Fact]
    public async Task Liveness_is_healthy_while_the_child_runs()
    {
        var probes = Create(ValkeyChildState.Running, new FakeClient());

        var result = await probes.LivenessAsync(TestContext.Current.CancellationToken);

        Assert.True(result.Healthy);
        Assert.Equal(ValkeyProbeKind.Liveness, result.Kind);
        Assert.Null(result.Reason);
    }

    [Fact]
    public async Task Liveness_is_healthy_when_the_server_is_external_and_not_supervised()
    {
        var probes = Create(ValkeyChildState.Disabled, new FakeClient());

        var result = await probes.LivenessAsync(TestContext.Current.CancellationToken);

        Assert.True(result.Healthy);
    }

    [Fact]
    public async Task Liveness_names_the_child_state_when_it_is_backing_off()
    {
        var probes = Create(ValkeyChildState.Backoff, new FakeClient());

        var result = await probes.LivenessAsync(TestContext.Current.CancellationToken);

        Assert.False(result.Healthy);
        Assert.Equal("managed child is Backoff", result.Reason);
        Assert.False(result.Critical);
    }

    [Fact]
    public async Task Readiness_passes_after_a_ping_and_a_reserved_key_round_trip_and_is_critical()
    {
        var client = new FakeClient();
        var probes = Create(ValkeyChildState.Running, client);

        var result = await probes.ReadinessAsync(TestContext.Current.CancellationToken);

        Assert.True(result.Healthy);
        Assert.True(result.Critical);
        Assert.Equal(1, client.PingCount);
        Assert.Contains("bazaar:health:readiness", client.Store.Keys);
    }

    [Fact]
    public async Task Readiness_reports_an_unreachable_server_by_exception_type_only()
    {
        var client = new FakeClient { Failure = new IOException($"no route to {_endpoint}") };
        var probes = Create(ValkeyChildState.Running, client);

        var result = await probes.ReadinessAsync(TestContext.Current.CancellationToken);

        Assert.False(result.Healthy);
        Assert.True(result.Critical);
        Assert.Equal("probe failed (IOException)", result.Reason);
        Assert.DoesNotContain(_endpoint, result.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Readiness_fails_when_the_round_trip_returns_other_bytes()
    {
        var client = new FakeClient { ReturnsOtherBytes = true };
        var probes = Create(ValkeyChildState.Running, client);

        var result = await probes.ReadinessAsync(TestContext.Current.CancellationToken);

        Assert.False(result.Healthy);
        Assert.Equal("round trip on the readiness key did not match", result.Reason);
    }

    [Fact]
    public async Task Dependencies_pass_when_the_server_has_no_memory_limit()
    {
        var client = new FakeClient { Memory = new ValkeyMemoryInfo(500, 0) };
        var probes = Create(ValkeyChildState.Running, client);

        var result = await probes.DependenciesAsync(TestContext.Current.CancellationToken);

        Assert.True(result.Healthy);
        Assert.False(result.Critical);
    }

    [Fact]
    public async Task Dependencies_pass_below_the_warn_percentage()
    {
        var client = new FakeClient { Memory = new ValkeyMemoryInfo(50, 100) };
        var probes = Create(ValkeyChildState.Running, client, warnPercent: 80);

        var result = await probes.DependenciesAsync(TestContext.Current.CancellationToken);

        Assert.True(result.Healthy);
    }

    [Fact]
    public async Task Dependencies_turn_amber_at_the_warn_percentage_without_being_critical()
    {
        var client = new FakeClient { Memory = new ValkeyMemoryInfo(85, 100) };
        var probes = Create(ValkeyChildState.Running, client, warnPercent: 80);

        var result = await probes.DependenciesAsync(TestContext.Current.CancellationToken);

        Assert.False(result.Healthy);
        Assert.False(result.Critical);
        Assert.Equal("memory at 85% of the limit (warns at 80%)", result.Reason);
    }

    [Fact]
    public async Task Self_test_round_trips_and_removes_its_key()
    {
        var client = new FakeClient();
        var probes = Create(ValkeyChildState.Running, client);

        var result = await probes.SelfTestAsync(TestContext.Current.CancellationToken);

        Assert.True(result.Healthy);
        Assert.Equal(ValkeyProbeKind.SelfTest, result.Kind);
        Assert.Empty(client.Store);
    }

    [Fact]
    public async Task Self_test_fails_when_the_server_fails_the_round_trip()
    {
        var client = new FakeClient { ReturnsOtherBytes = true };
        var probes = Create(ValkeyChildState.Running, client);

        var result = await probes.SelfTestAsync(TestContext.Current.CancellationToken);

        Assert.False(result.Healthy);
        Assert.Equal("round trip on the self-test key did not match", result.Reason);
    }

    [Fact]
    public async Task Caller_cancellation_is_not_reported_as_a_probe_result()
    {
        var probes = Create(ValkeyChildState.Running, new FakeClient());
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => probes.ReadinessAsync(cancelled.Token));
    }

    private static ValkeyHealthProbes Create(ValkeyChildState state, FakeClient client, int warnPercent = 80) =>
        new(new FakeChildStatus(state), client, new ValkeyCacheOptions { MemoryWarnPercent = warnPercent });

    private sealed class FakeChildStatus(ValkeyChildState state) : IValkeyChildStatus
    {
        public ValkeyChildState State => state;

        public int Restarts => 0;
    }

    private sealed class FakeClient : IValkeyClient
    {
        public Exception? Failure { get; init; }

        public bool ReturnsOtherBytes { get; init; }

        public ValkeyMemoryInfo Memory { get; init; } = new(0, 0);

        public int PingCount { get; private set; }

        public Dictionary<string, byte[]> Store { get; } = new(StringComparer.Ordinal);

        public Task PingAsync(CancellationToken ct)
        {
            PingCount++;
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

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private void ThrowIfFailing()
        {
            if (Failure is not null)
            {
                throw Failure;
            }
        }
    }
}
