using Bazaar.Contracts;
using Bazaar.Server.Host.Composition;
using Bazaar.Server.Host.Startup;
using Bazaar.Server.Providers;
using Bazaar.Server.WebAdmin;
using Microsoft.Extensions.DependencyInjection;

namespace Bazaar.Server.Host.Tests;

public class HostAdminStatusSourceTests
{
    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Fact]
    public async Task Reports_version_uptime_and_non_secret_lines()
    {
        var time = new FixedTime(DateTimeOffset.UnixEpoch);
        var state = new HostState(time);
        time.Now = DateTimeOffset.UnixEpoch.AddSeconds(90);

        var view = await new HostAdminStatusSource(state, new ProviderCapabilityReport([])).GetAsync(TestContext.Current.CancellationToken);

        Assert.Equal("Bazaar", view.DisplayName);
        Assert.Equal(ProductVersion.Informational, view.Version);
        Assert.Matches(@"^\d+\.\d+\.\d+", view.Version);
        Assert.Equal(DateTimeOffset.UnixEpoch, view.StartedAt);
        Assert.Equal(90, view.UptimeSeconds);
        Assert.Null(view.Codename);
        Assert.Contains(view.ProviderLines, l => l.StartsWith("os: ", StringComparison.Ordinal));
        Assert.Contains(view.ProviderLines, l => l.StartsWith("runtime: ", StringComparison.Ordinal));
    }

    [Fact]
    public void Is_registered_by_the_host_runtime()
    {
        var services = new ServiceCollection();
        services.AddBazaarHostRuntime(new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build());
        Assert.Contains(services, d => d.ServiceType == typeof(IAdminStatusSource) && d.ImplementationType == typeof(HostAdminStatusSource));
    }

    private sealed class Queues(params AdminQueueDepth[] rows) : IAdminQueueDepthSource
    {
        public Task<IReadOnlyList<AdminQueueDepth>> GetAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<AdminQueueDepth>>(rows);
    }

    private sealed class Checks(params AdminHealthCheck[] rows) : IAdminHealthSource
    {
        public Task<IReadOnlyList<AdminHealthCheck>> GetAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<AdminHealthCheck>>(rows);
    }

    private static HostAdminStatusSource Source(IEnumerable<IAdminQueueDepthSource>? q, IEnumerable<IAdminHealthSource>? h) =>
        new(new HostState(TimeProvider.System), new ProviderCapabilityReport([]), q, h);

    [Fact]
    public async Task With_no_contributors_queues_are_empty_and_health_is_unknown()
    {
        var view = await Source(null, null).GetAsync(TestContext.Current.CancellationToken);
        Assert.Empty(view.Queues!);
        Assert.Equal("unknown", view.Health!.State);
    }

    [Fact]
    public async Task Merges_queue_depths_from_every_source()
    {
        var view = await Source([new Queues(new AdminQueueDepth("jobs", 3)), new Queues(new AdminQueueDepth("mail", 0))], null).GetAsync(TestContext.Current.CancellationToken);
        Assert.Equal(["jobs", "mail"], view.Queues!.Select(q => q.Name).ToArray());
        Assert.Equal(3, view.Queues![0].Depth);
    }

    [Theory]
    [InlineData("ok-only", "healthy")]
    [InlineData("degraded", "degraded")]
    [InlineData("failing", "failing")]
    public async Task Health_summary_follows_the_worst_check(string kind, string expected)
    {
        AdminHealthCheck[] rows = kind switch
        {
            "ok-only" => [new("db", "healthy", null)],
            "degraded" => [new("db", "healthy", null), new("mail", "degraded", "slow")],
            _ => [new("db", "degraded", null), new("mail", "failing", "down")],
        };
        var view = await Source(null, [new Checks(rows)]).GetAsync(TestContext.Current.CancellationToken);
        Assert.Equal(expected, view.Health!.State);
        Assert.Equal(rows.Length, view.Health.Checks.Count);
    }
}
