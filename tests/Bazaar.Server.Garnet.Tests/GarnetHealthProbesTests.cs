using Bazaar.Server.Valkey;

namespace Bazaar.Server.Garnet.Tests;

/// <summary>The four probes (doc 42 section 11, doc 40 section 4b.2). They report results and never throw on a failed server call.</summary>
public class GarnetHealthProbesTests
{
    private const string _endpoint = "cache.internal:6379";
    private const long _capBytes = 256L * 1024 * 1024;

    [Fact]
    public async Task Liveness_is_healthy_while_the_child_runs()
    {
        var probes = Create(ValkeyChildState.Running, new TestClientFactory());

        var result = await probes.LivenessAsync(TestContext.Current.CancellationToken);

        Assert.True(result.Healthy);
        Assert.Equal(ValkeyProbeKind.Liveness, result.Kind);
        Assert.Null(result.Reason);
    }

    [Fact]
    public async Task Liveness_is_healthy_when_the_server_is_external_and_not_supervised()
    {
        var probes = Create(ValkeyChildState.Disabled, new TestClientFactory());

        var result = await probes.LivenessAsync(TestContext.Current.CancellationToken);

        Assert.True(result.Healthy);
    }

    [Fact]
    public async Task Liveness_names_the_child_state_when_it_is_backing_off()
    {
        var probes = Create(ValkeyChildState.Backoff, new TestClientFactory());

        var result = await probes.LivenessAsync(TestContext.Current.CancellationToken);

        Assert.False(result.Healthy);
        Assert.Equal("managed child is Backoff", result.Reason);
    }

    [Fact]
    public async Task Readiness_passes_after_a_ping_and_a_reserved_key_round_trip()
    {
        var factory = new TestClientFactory();
        var probes = Create(ValkeyChildState.Running, factory);

        var result = await probes.ReadinessAsync(TestContext.Current.CancellationToken);

        Assert.True(result.Healthy);
        Assert.Contains("bazaar:health:readiness", factory.Clients.Single().Store.Keys);
    }

    [Fact]
    public async Task Every_probe_is_non_critical_so_the_cache_never_gates_readiness()
    {
        var probes = Create(ValkeyChildState.Running, new TestClientFactory { Configure = c => c.Failure = new IOException("down") });
        var ct = TestContext.Current.CancellationToken;

        var results = new[]
        {
            await probes.LivenessAsync(ct),
            await probes.ReadinessAsync(ct),
            await probes.DependenciesAsync(ct),
            await probes.SelfTestAsync(ct),
        };

        Assert.All(results, r => Assert.False(r.Critical));
    }

    [Fact]
    public async Task Readiness_reports_an_unreachable_server_by_exception_type_only()
    {
        var factory = new TestClientFactory { Configure = c => c.Failure = new IOException($"no route to {_endpoint}") };
        var probes = Create(ValkeyChildState.Running, factory);

        var result = await probes.ReadinessAsync(TestContext.Current.CancellationToken);

        Assert.False(result.Healthy);
        Assert.Equal("probe failed (IOException)", result.Reason);
        Assert.DoesNotContain(_endpoint, result.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Probes_report_a_connection_that_is_not_available_without_connecting()
    {
        var factory = new TestClientFactory();
        var probes = Create(ValkeyChildState.Running, factory, source: new TestConnectionSource { Value = null });

        var result = await probes.ReadinessAsync(TestContext.Current.CancellationToken);

        Assert.False(result.Healthy);
        Assert.Equal("probe failed (InvalidOperationException)", result.Reason);
        Assert.Empty(factory.ConnectionStrings);
    }

    [Fact]
    public async Task Readiness_fails_when_the_round_trip_returns_other_bytes()
    {
        var factory = new TestClientFactory { Configure = c => c.ReturnsOtherBytes = true };
        var probes = Create(ValkeyChildState.Running, factory);

        var result = await probes.ReadinessAsync(TestContext.Current.CancellationToken);

        Assert.False(result.Healthy);
        Assert.Equal("round trip on the readiness key did not match", result.Reason);
    }

    [Fact]
    public async Task Dependencies_pass_when_the_server_has_no_memory_limit()
    {
        var probes = Create(ValkeyChildState.Running, new TestClientFactory { Configure = c => c.Memory = new ValkeyMemoryInfo(500, 0) });

        var result = await probes.DependenciesAsync(TestContext.Current.CancellationToken);

        Assert.True(result.Healthy);
    }

    [Fact]
    public async Task Dependencies_pass_below_the_warn_percentage()
    {
        var probes = Create(ValkeyChildState.Running, new TestClientFactory { Configure = c => c.Memory = new ValkeyMemoryInfo(50, 100) }, warnPercent: 80);

        var result = await probes.DependenciesAsync(TestContext.Current.CancellationToken);

        Assert.True(result.Healthy);
    }

    [Fact]
    public async Task Dependencies_turn_amber_at_the_warn_percentage()
    {
        var probes = Create(ValkeyChildState.Running, new TestClientFactory { Configure = c => c.Memory = new ValkeyMemoryInfo(85, 100) }, warnPercent: 80);

        var result = await probes.DependenciesAsync(TestContext.Current.CancellationToken);

        Assert.False(result.Healthy);
        Assert.Equal("memory at 85% of the limit (warns at 80%)", result.Reason);
    }

    [Fact]
    public async Task Self_test_round_trips_and_removes_its_key()
    {
        var factory = new TestClientFactory();
        var probes = Create(ValkeyChildState.Running, factory);

        var result = await probes.SelfTestAsync(TestContext.Current.CancellationToken);

        Assert.True(result.Healthy);
        Assert.Equal(ValkeyProbeKind.SelfTest, result.Kind);
        Assert.Empty(factory.Clients.Single().Store);
    }

    [Fact]
    public async Task Self_test_fails_when_the_server_fails_the_round_trip()
    {
        var factory = new TestClientFactory { Configure = c => c.ReturnsOtherBytes = true };
        var probes = Create(ValkeyChildState.Running, factory);

        var result = await probes.SelfTestAsync(TestContext.Current.CancellationToken);

        Assert.False(result.Healthy);
        Assert.Equal("round trip on the self-test key did not match", result.Reason);
    }

    [Fact]
    public async Task Managed_server_is_measured_against_the_configured_memory_when_it_reports_no_limit()
    {
        var probes = Create(ValkeyChildState.Running, new TestClientFactory { Configure = c => c.Memory = new ValkeyMemoryInfo(_capBytes * 9 / 10, 0) }, managed: true);

        var result = await probes.DependenciesAsync(TestContext.Current.CancellationToken);

        Assert.False(result.Healthy);
        Assert.StartsWith("memory at 90%", result.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_server_that_reports_no_memory_figure_passes_the_dependencies_probe()
    {
        var probes = Create(ValkeyChildState.Running, new TestClientFactory { Configure = c => c.Memory = new ValkeyMemoryInfo(0, 0) }, managed: true);

        var result = await probes.DependenciesAsync(TestContext.Current.CancellationToken);

        Assert.True(result.Healthy);
    }

    [Fact]
    public async Task Caller_cancellation_is_not_reported_as_a_probe_result()
    {
        var probes = Create(ValkeyChildState.Running, new TestClientFactory());
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => probes.ReadinessAsync(cancelled.Token));
    }

    private static GarnetHealthProbes Create(
        ValkeyChildState state,
        TestClientFactory factory,
        int warnPercent = 80,
        bool managed = false,
        TestConnectionSource? source = null)
    {
        var options = new GarnetCacheOptions { MemoryWarnPercent = warnPercent, Managed = managed, MaxMemory = "256m" };
        return new GarnetHealthProbes(new FakeChildStatus(state), source ?? new TestConnectionSource { Value = _endpoint }, factory, options);
    }

    private sealed class FakeChildStatus(ValkeyChildState state) : IValkeyChildStatus
    {
        public ValkeyChildState State => state;

        public int Restarts => 0;
    }
}
