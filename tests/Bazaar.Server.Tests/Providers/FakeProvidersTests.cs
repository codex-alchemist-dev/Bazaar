using Bazaar.Server.Providers;
using Bazaar.Server.Providers.Fakes;

namespace Bazaar.Server.Tests.Providers;

public class FakeProvidersTests
{
    [Fact]
    public void Service_host_probe_is_usable_by_default()
    {
        var status = new FakeServiceHostProvider().Probe();

        Assert.True(status.Usable);
        Assert.Null(status.Reason);
        Assert.Equal("fake_service_host", status.Kind);
    }

    [Fact]
    public void Service_host_probe_reports_given_reason_when_unusable()
    {
        var status = new FakeServiceHostProvider(usable: false, reason: "no scm").Probe();

        Assert.False(status.Usable);
        Assert.Equal("no scm", status.Reason);
    }

    [Fact]
    public void Vm_probe_is_usable_by_default()
    {
        var status = new FakeVmProvider().Probe();

        Assert.True(status.Usable);
        Assert.Null(status.Reason);
        Assert.Equal("fake_vm", status.Kind);
    }

    [Fact]
    public void Vm_probe_reports_default_reason_when_unusable()
    {
        var status = new FakeVmProvider(usable: false).Probe();

        Assert.False(status.Usable);
        Assert.Equal("fake provider unavailable", status.Reason);
    }

    [Fact]
    public void Cache_probe_is_usable_by_default()
    {
        var status = new FakeCacheProvider().Probe();

        Assert.True(status.Usable);
        Assert.Null(status.Reason);
        Assert.Equal("fake_cache", status.Kind);
    }

    [Fact]
    public void Cache_probe_reports_default_reason_when_unusable()
    {
        var status = new FakeCacheProvider(usable: false).Probe();

        Assert.False(status.Usable);
        Assert.Equal("fake provider unavailable", status.Reason);
    }

    [Fact]
    public void Search_probe_is_usable_by_default()
    {
        var status = new FakeSearchProvider().Probe();

        Assert.True(status.Usable);
        Assert.Null(status.Reason);
        Assert.Equal("fake_search", status.Kind);
    }

    [Fact]
    public void Search_probe_reports_default_reason_when_unusable()
    {
        var status = new FakeSearchProvider(usable: false).Probe();

        Assert.False(status.Usable);
        Assert.Equal("fake provider unavailable", status.Reason);
    }

    [Fact]
    public async Task Cache_get_returns_the_stored_bytes()
    {
        var cache = new FakeCacheProvider(clock: new TestClock(DateTimeOffset.UtcNow));
        var bytes = new byte[] { 1, 2, 3 };

        await cache.SetAsync("k", bytes, TimeSpan.FromMinutes(5), CancellationToken.None);
        var got = await cache.GetAsync("k", CancellationToken.None);

        Assert.NotNull(got);
        Assert.Equal(bytes, got.Value.ToArray());
    }

    [Fact]
    public async Task Cache_get_returns_null_after_ttl_passes()
    {
        var clock = new TestClock(DateTimeOffset.UtcNow);
        var cache = new FakeCacheProvider(clock: clock);

        await cache.SetAsync("k", new byte[] { 9 }, TimeSpan.FromMinutes(1), CancellationToken.None);
        clock.Advance(TimeSpan.FromMinutes(2));
        var got = await cache.GetAsync("k", CancellationToken.None);

        Assert.Null(got);
    }

    [Fact]
    public async Task Cache_get_of_unknown_key_returns_null()
    {
        var cache = new FakeCacheProvider();

        var got = await cache.GetAsync("missing", CancellationToken.None);

        Assert.Null(got);
    }

    [Fact]
    public async Task Search_returns_only_hits_in_the_query_scope()
    {
        var search = new FakeSearchProvider();
        await search.IndexAsync(new SearchDocument("1", "a", "Hello World"), CancellationToken.None);
        await search.IndexAsync(new SearchDocument("2", "a", "hello there"), CancellationToken.None);
        await search.IndexAsync(new SearchDocument("3", "b", "hello elsewhere"), CancellationToken.None);

        var hits = await search.QueryAsync(new SearchQuery("a", "hello", 10), CancellationToken.None);

        Assert.Equal("1,2", string.Join(",", hits.Select(h => h.Id)));
        Assert.All(hits, h => Assert.Equal(1.0, h.Score));
    }

    [Fact]
    public async Task Search_limit_one_returns_one_hit()
    {
        var search = new FakeSearchProvider();
        await search.IndexAsync(new SearchDocument("1", "a", "hello"), CancellationToken.None);
        await search.IndexAsync(new SearchDocument("2", "a", "hello"), CancellationToken.None);

        var hits = await search.QueryAsync(new SearchQuery("a", "hello", 1), CancellationToken.None);

        Assert.Single(hits);
    }

    [Fact]
    public async Task Search_limit_zero_returns_no_hits()
    {
        var search = new FakeSearchProvider();
        await search.IndexAsync(new SearchDocument("1", "a", "hello"), CancellationToken.None);

        var hits = await search.QueryAsync(new SearchQuery("a", "hello", 0), CancellationToken.None);

        Assert.Empty(hits);
    }

    [Fact]
    public async Task Service_host_recovery_reports_configured_with_three_actions()
    {
        var report = await new FakeServiceHostProvider().RecoveryAsync(CancellationToken.None);

        Assert.True(report.Configured);
        Assert.Equal(3, report.Actions.Count);
    }

    private sealed class TestClock(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }
}
