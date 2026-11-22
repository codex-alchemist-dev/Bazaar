using Bazaar.Contracts.Gateway;
using Bazaar.Server.Gateway.Selection;

namespace Bazaar.Server.Tests.Gateway.Pipeline;

/// <summary>The gateway seam of doc 92: a ranker may reorder or omit the resolved candidates and nothing else.</summary>
public class EndpointSelectionGatewayTests
{
    private static readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    private sealed class FixedRanker(Func<IReadOnlyList<Endpoint>, IReadOnlyList<Endpoint>?> order) : ICandidateRanker
    {
        public string? RouteSeen { get; private set; }

        public IReadOnlyList<Endpoint>? Order(string routeId, IReadOnlyList<Endpoint> candidates, SelectionRequest request)
        {
            RouteSeen = routeId;
            return order(candidates);
        }
    }

    [Fact]
    public async Task No_ranker_keeps_the_route_order()
    {
        var h = new GatewayHarness();
        h.Adapter("a").Provider.EnqueueCall(GatewayHarness.Reply());

        await GatewayHarness.CollectAsync(h.Gateway().CallAsync(GatewayHarness.Request(), _ct), _ct);

        Assert.Single(h.Adapter("a").Endpoints);
        Assert.Empty(h.Adapter("b").Endpoints);
    }

    [Fact]
    public async Task A_ranker_that_returns_null_keeps_the_route_order()
    {
        var h = new GatewayHarness();
        h.Adapter("a").Provider.EnqueueCall(GatewayHarness.Reply());
        var ranker = new FixedRanker(_ => null);

        await GatewayHarness.CollectAsync(h.Gateway(ranker: ranker).CallAsync(GatewayHarness.Request(), _ct), _ct);

        Assert.Equal("r", ranker.RouteSeen);
        Assert.Single(h.Adapter("a").Endpoints);
    }

    [Fact]
    public async Task The_ranker_order_decides_which_candidate_is_called_first()
    {
        var h = new GatewayHarness();
        h.Adapter("b").Provider.EnqueueCall(GatewayHarness.Reply("from b"));

        var events = await GatewayHarness.CollectAsync(
            h.Gateway(ranker: new FixedRanker(c => [.. c.Reverse()])).CallAsync(GatewayHarness.Request(), _ct), _ct);

        Assert.Contains(events, e => e is ModelEvent.TextDelta { Text: "from b" });
        Assert.Empty(h.Adapter("a").Endpoints);
        Assert.Equal("b", Assert.Single(h.Usage.Records).ProviderId);
    }

    [Fact]
    public async Task A_ranker_cannot_add_a_candidate_that_policy_did_not_resolve()
    {
        var h = new GatewayHarness();
        h.Adapter("a").Provider.EnqueueCall(GatewayHarness.Reply());
        var outsider = new Endpoint(new EndpointKey("c", "m"));

        await GatewayHarness.CollectAsync(
            h.Gateway(ranker: new FixedRanker(c => [outsider, .. c])).CallAsync(GatewayHarness.Request(), _ct), _ct);

        Assert.Empty(h.Adapter("c").Endpoints);
        Assert.Single(h.Adapter("a").Endpoints);
    }

    [Fact]
    public async Task A_ranker_that_throws_is_ignored_and_reported()
    {
        var h = new GatewayHarness();
        h.Adapter("a").Provider.EnqueueCall(GatewayHarness.Reply());

        var events = await GatewayHarness.CollectAsync(
            h.Gateway(ranker: new FixedRanker(_ => throw new InvalidOperationException("boom"))).CallAsync(GatewayHarness.Request(), _ct), _ct);

        Assert.IsType<ModelEvent.Done>(events[^1]);
        Assert.Single(h.InternalErrors);
    }

    [Fact]
    public async Task A_ranker_that_repeats_a_candidate_is_ignored()
    {
        var h = new GatewayHarness();
        h.Adapter("a").Provider.EnqueueCall(GatewayHarness.Reply());

        await GatewayHarness.CollectAsync(
            h.Gateway(ranker: new FixedRanker(c => [c[1], c[1]])).CallAsync(GatewayHarness.Request(), _ct), _ct);

        Assert.Single(h.Adapter("a").Endpoints);
    }

    [Fact]
    public async Task Failover_walks_the_ranked_order()
    {
        var h = new GatewayHarness();
        h.Adapter("b").Provider.EnqueueCall([new Bazaar.Server.Gateway.Fakes.ScriptStep.Emit(new ModelEvent.Failure("provider_error", "down"))]);
        h.Adapter("a").Provider.EnqueueCall(GatewayHarness.Reply("from a"));

        var events = await GatewayHarness.CollectAsync(
            h.Gateway(ranker: new FixedRanker(c => [.. c.Reverse()])).CallAsync(GatewayHarness.Request(), _ct), _ct);

        Assert.Contains(events, e => e is ModelEvent.TextDelta { Text: "from a" });
        Assert.Single(h.Adapter("b").Endpoints);
        Assert.Single(h.Adapter("a").Endpoints);
    }

    [Fact]
    public async Task The_real_selector_prefers_the_faster_endpoint_and_learns_from_each_call()
    {
        var h = new GatewayHarness();
        var stats = new EndpointStatsTracker(time: h.Clock);
        var ranker = new ProfileBoundRanker(new EndpointSelector(stats, time: h.Clock, random: new SeededSelectionRandom(1)));
        await ranker.BindAsync("r", SelectionProfiles.Latency, _ct);
        for (var i = 0; i < 8; i++)
        {
            stats.Begin(new EndpointKey("a", "m")).Complete(new AttemptOutcome(AttemptOutcomeKind.Success, TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(3)));
            stats.Begin(new EndpointKey("b", "m")).Complete(new AttemptOutcome(AttemptOutcomeKind.Success, TimeSpan.FromMilliseconds(200), TimeSpan.FromMilliseconds(200)));
        }

        var picked = new List<string>();
        for (var i = 0; i < 3; i++)
        {
            h.Adapter("a").Provider.EnqueueCall(GatewayHarness.Reply());
            h.Adapter("b").Provider.EnqueueCall(GatewayHarness.Reply());
            await GatewayHarness.CollectAsync(h.Gateway(ranker: ranker, stats: stats).CallAsync(GatewayHarness.Request(), _ct), _ct);
            picked.Add(h.Usage.Records[^1].ProviderId);
        }

        Assert.Contains("b", picked);
        Assert.True(stats.Snapshot(new EndpointKey("b", "m")).Successes > 8, "the gateway reports outcomes back to the tracker");
        Assert.Equal(0, stats.Snapshot(new EndpointKey("b", "m")).InFlight);
    }

    [Fact]
    public async Task A_quota_blocked_endpoint_is_ranked_last_and_skipped_with_its_reason()
    {
        var h = new GatewayHarness();
        h.Quota.Blocked[(GatewayHarness.KeyOf("b"), "claude")] = (QuotaState.RateLimited, h.Clock.GetUtcNow().AddMinutes(1));
        h.Adapter("a").Provider.EnqueueCall(GatewayHarness.Reply());
        var stats = new EndpointStatsTracker(time: h.Clock);
        var ranker = new ProfileBoundRanker(new EndpointSelector(stats, time: h.Clock));
        await ranker.BindAsync("r", SelectionProfiles.Balanced, _ct);

        var events = await GatewayHarness.CollectAsync(h.Gateway(ranker: ranker, stats: stats).CallAsync(GatewayHarness.Request(), _ct), _ct);

        Assert.IsType<ModelEvent.Done>(events[^1]);
        Assert.Empty(h.Adapter("b").Endpoints);
    }

    [Fact]
    public async Task The_price_table_feeds_the_cost_factor()
    {
        var h = new GatewayHarness();
        var prices = new Bazaar.Server.Gateway.Usage.PriceTable("t", [
            new Bazaar.Server.Gateway.Usage.PriceEntry("a", "m", new DateOnly(2026, 1, 1), 15m, 75m),
            new Bazaar.Server.Gateway.Usage.PriceEntry("b", "m", new DateOnly(2026, 1, 1), 1m, 5m)]);
        var stats = new EndpointStatsTracker(time: h.Clock);
        var ranker = new ProfileBoundRanker(new EndpointSelector(stats, time: h.Clock, random: new SeededSelectionRandom(2)));
        await ranker.BindAsync("r", SelectionProfiles.Cost, _ct);
        h.Adapter("a").Provider.EnqueueCall(GatewayHarness.Reply());
        h.Adapter("b").Provider.EnqueueCall(GatewayHarness.Reply());

        await GatewayHarness.CollectAsync(h.Gateway(prices: prices, ranker: ranker, stats: stats).CallAsync(GatewayHarness.Request(), _ct), _ct);

        Assert.Equal("b", Assert.Single(h.Usage.Records).ProviderId);
    }
}
