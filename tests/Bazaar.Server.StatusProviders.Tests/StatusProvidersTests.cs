using Bazaar.Server.WebAdmin;

namespace Bazaar.Server.StatusProviders.Tests;

public sealed class StatusProvidersTests
{
    private sealed class Facts(AgentFacts? agents = null, GatewayFacts? gateway = null, GcFacts? gc = null, bool throws = false) : IStatusFactsSource
    {
        public Task<AgentFacts> GetAgentsAsync(CancellationToken ct) => throws ? throw new InvalidOperationException("secret connection string") : Task.FromResult(agents ?? new AgentFacts(3, 4, 1, 1, 0, 5));

        public Task<GatewayFacts> GetGatewayAsync(CancellationToken ct) => throws ? throw new InvalidOperationException("x") : Task.FromResult(gateway ?? new GatewayFacts(2, 0, 0));

        public Task<GcFacts> GetGcAsync(CancellationToken ct) => throws ? throw new InvalidOperationException("x") : Task.FromResult(gc ?? new GcFacts(7, 2));
    }

    [Fact]
    public async Task Queue_source_reports_inbox_and_running_turns()
    {
        var rows = await new AgentQueueDepthSource(new Facts()).GetAsync(TestContext.Current.CancellationToken);
        Assert.Equal([("agent-inbox-pending", 4L), ("agent-inbox-claimed", 1L), ("agent-turns-running", 1L)], rows.Select(r => (r.Name, r.Depth)).ToArray());
    }

    [Fact]
    public async Task Queue_source_returns_nothing_when_the_database_throws()
    {
        Assert.Empty(await new AgentQueueDepthSource(new Facts(throws: true)).GetAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Health_source_reports_three_checks_and_hides_exception_text()
    {
        var ok = await new CoreHealthSource(new Facts()).GetAsync(TestContext.Current.CancellationToken);
        Assert.Equal(["gc-log", "gateway", "agents"], ok.Select(c => c.Name).ToArray());
        Assert.All(ok, c => Assert.Equal("healthy", c.State));

        var down = await new CoreHealthSource(new Facts(throws: true)).GetAsync(TestContext.Current.CancellationToken);
        Assert.All(down, c => Assert.Equal("failing", c.State));
        Assert.DoesNotContain(down, c => c.Detail!.Contains("secret"));
    }

    [Theory]
    [InlineData(0, 0, 0, "unknown")]
    [InlineData(2, 0, 0, "healthy")]
    [InlineData(2, 0, 1, "degraded")]
    [InlineData(2, 1, 1, "degraded")]
    [InlineData(2, 2, 2, "failing")]
    public void Gateway_state(long enabled, long down, long limited, string expected) =>
        Assert.Equal(expected, CoreHealthSource.EvaluateGateway(new GatewayFacts(enabled, down, limited)).State);

    [Theory]
    [InlineData(0, 0, 0, "unknown")]
    [InlineData(3, 0, 0, "healthy")]
    [InlineData(3, 2, 0, "degraded")]
    [InlineData(3, 0, 601, "degraded")]
    [InlineData(3, 0, 600, "healthy")]
    public void Agents_state(long agents, long failed, long oldest, string expected) =>
        Assert.Equal(expected, CoreHealthSource.EvaluateAgents(new AgentFacts(agents, 0, 0, 0, failed, oldest)).State);
}
