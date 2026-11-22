using Bazaar.Contracts.Gateway;
using Bazaar.Server.Gateway.Fakes;
using Bazaar.Server.Gateway.Health;
using Bazaar.Server.Gateway.Pipeline;
using Bazaar.Server.Tests.Gateway.Policy;
using Bazaar.Testing;

namespace Bazaar.Server.Tests.Gateway.Pipeline;

/// <summary>
/// The gateway with the real <see cref="RetryAfterQuotaTracker"/>: spend caps reroute, retry-after blocks expire, and
/// <see cref="AgentCapacityChanged"/> is published only when every candidate of an agent is blocked.
/// </summary>
public class QuotaBridgeTests
{
    private const string _otherAgent = "juniper";

    private static readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    private static ModelEvent.Failure OnlyFailure(IEnumerable<ModelEvent> events) =>
        Assert.IsType<ModelEvent.Failure>(Assert.Single(events));

    private static GatewayHarness Harness()
    {
        var h = new GatewayHarness();
        h.Clock.SetUtcNow(new DateTimeOffset(2026, 11, 15, 12, 0, 0, TimeSpan.Zero));
        h.World.AddPolicy(_otherAgent, ["a:m", "b:m", "r"], "r");
        return h;
    }

    [Fact]
    public async Task Spend_capped_source_is_rerouted_and_not_retried_while_capped()
    {
        var h = Harness();
        var quota = new RetryAfterQuotaTracker(h.Clock);
        var gateway = h.Gateway(quota: quota);
        h.Adapter("a").Provider.EnqueueCall(new ScriptStep.SpendCapped());
        h.Adapter("b").Provider.EnqueueCall(GatewayHarness.Reply());
        h.Adapter("b").Provider.EnqueueCall(GatewayHarness.Reply());

        var first = await GatewayHarness.CollectAsync(gateway.CallAsync(GatewayHarness.Request(), _ct), _ct);
        var second = await GatewayHarness.CollectAsync(gateway.CallAsync(GatewayHarness.Request(), _ct), _ct);

        Assert.IsType<ModelEvent.Done>(first[^1]);
        Assert.IsType<ModelEvent.Done>(second[^1]);
        Assert.Single(h.Adapter("a").Provider.Calls);
        Assert.Equal(2, h.Adapter("b").Provider.Calls.Count);
        Assert.Equal(QuotaState.SpendCapped, quota.BlockedState(GatewayHarness.KeyOf("a"), "claude"));
        Assert.Empty(h.Sink.Events);
    }

    [Fact]
    public async Task Spend_cap_on_every_source_publishes_one_capacity_change_in_the_same_call()
    {
        var h = Harness();
        var quota = new RetryAfterQuotaTracker(h.Clock);
        var gateway = h.Gateway(quota: quota);
        h.Adapter("a").Provider.EnqueueCall(new ScriptStep.SpendCapped());
        h.Adapter("b").Provider.EnqueueCall(new ScriptStep.SpendCapped());

        var events = await GatewayHarness.CollectAsync(gateway.CallAsync(GatewayHarness.Request(), _ct), _ct);

        Assert.Equal("spend_capped", OnlyFailure(events).Code);
        var published = Assert.IsType<AgentCapacityChanged>(Assert.Single(h.Sink.Events));
        Assert.Equal((GatewayHarness.Agent, false, new DateTimeOffset(2026, 12, 1, 0, 0, 0, TimeSpan.Zero), QuotaState.SpendCapped),
            (published.AgentId, published.Available, published.Until, published.Reason));
    }

    [Fact]
    public async Task Retry_after_on_the_last_source_publishes_in_the_same_call_then_no_capacity_on_the_next()
    {
        var h = Harness();
        var gateway = h.Gateway(quota: new RetryAfterQuotaTracker(h.Clock));
        var start = h.Clock.GetUtcNow();
        h.Adapter("a").Provider.EnqueueCall(new ScriptStep.RateLimited(60));
        h.Adapter("b").Provider.EnqueueCall(new ScriptStep.RateLimited(30));

        var first = await GatewayHarness.CollectAsync(gateway.CallAsync(GatewayHarness.Request(), _ct), _ct);

        // The last candidate's own 429 is what blocks the route, so the change is published by this call.
        Assert.Equal("rate_limited", OnlyFailure(first).Code);
        var published = Assert.IsType<AgentCapacityChanged>(Assert.Single(h.Sink.Events));
        Assert.Equal((false, start.AddSeconds(30), QuotaState.RateLimited), (published.Available, published.Until, published.Reason));

        var second = await GatewayHarness.CollectAsync(gateway.CallAsync(GatewayHarness.Request(), _ct), _ct);

        Assert.Equal("no_capacity", OnlyFailure(second).Code);
        Assert.Single(h.Sink.Events);
    }

    [Fact]
    public async Task One_blocked_source_with_another_available_publishes_nothing()
    {
        var h = Harness();
        var gateway = h.Gateway(quota: new RetryAfterQuotaTracker(h.Clock));
        h.Adapter("a").Provider.EnqueueCall(new ScriptStep.RateLimited(60));
        h.Adapter("b").Provider.EnqueueCall(GatewayHarness.Reply());

        var events = await GatewayHarness.CollectAsync(gateway.CallAsync(GatewayHarness.Request(), _ct), _ct);

        Assert.IsType<ModelEvent.Done>(events[^1]);
        Assert.Empty(h.Sink.Events);
    }

    [Fact]
    public async Task Last_source_failing_without_a_block_publishes_nothing()
    {
        var h = Harness();
        var gateway = h.Gateway(quota: new RetryAfterQuotaTracker(h.Clock));
        h.Adapter("a").Provider.EnqueueCall(new ScriptStep.RateLimited(60));
        h.Adapter("b").Provider.EnqueueCall(new ScriptStep.Emit(new ModelEvent.Failure("provider_error", "upstream fault")));

        var events = await GatewayHarness.CollectAsync(gateway.CallAsync(GatewayHarness.Request(), _ct), _ct);

        Assert.Equal("provider_error", OnlyFailure(events).Code);
        Assert.Empty(h.Sink.Events);
    }

    [Fact]
    public async Task Rate_limit_without_retry_after_gives_no_block_and_the_source_is_tried_again()
    {
        var h = Harness();
        var quota = new RetryAfterQuotaTracker(h.Clock);
        var gateway = h.Gateway(quota: quota);
        h.Adapter("a").Provider.EnqueueCall(new ScriptStep.RateLimited());
        h.Adapter("b").Provider.EnqueueCall(GatewayHarness.Reply());
        h.Adapter("a").Provider.EnqueueCall(GatewayHarness.Reply());

        await GatewayHarness.CollectAsync(gateway.CallAsync(GatewayHarness.Request(), _ct), _ct);
        Assert.Null(quota.BlockedState(GatewayHarness.KeyOf("a"), "claude"));

        var second = await GatewayHarness.CollectAsync(gateway.CallAsync(GatewayHarness.Request(), _ct), _ct);

        Assert.IsType<ModelEvent.Done>(second[^1]);
        Assert.Equal(2, h.Adapter("a").Provider.Calls.Count);
        Assert.Empty(h.Sink.Events);
    }

    [Fact]
    public async Task A_block_is_per_key_so_another_agent_sharing_the_key_is_rerouted_too()
    {
        var h = Harness();
        var quota = new RetryAfterQuotaTracker(h.Clock);
        var gateway = h.Gateway(quota: quota);
        h.Adapter("a").Provider.EnqueueCall(new ScriptStep.RateLimited(120));
        h.Adapter("b").Provider.EnqueueCall(GatewayHarness.Reply());
        h.Adapter("b").Provider.EnqueueCall(GatewayHarness.Reply());

        await GatewayHarness.CollectAsync(gateway.CallAsync(GatewayHarness.Request(), _ct), _ct);
        var other = await GatewayHarness.CollectAsync(gateway.CallAsync(InMemoryPolicyWorld.Request(_otherAgent, "r"), _ct), _ct);

        Assert.IsType<ModelEvent.Done>(other[^1]);
        Assert.Single(h.Adapter("a").Provider.Calls);
        Assert.Equal(2, h.Adapter("b").Provider.Calls.Count);
        Assert.Empty(h.Sink.Events);
    }

    [Fact]
    public async Task Capacity_change_names_the_agent_whose_candidates_are_all_blocked()
    {
        var h = Harness();
        var gateway = h.Gateway(quota: new RetryAfterQuotaTracker(h.Clock));
        h.Adapter("a").Provider.EnqueueCall(new ScriptStep.RateLimited(60));
        h.Adapter("b").Provider.EnqueueCall(new ScriptStep.RateLimited(60));

        await GatewayHarness.CollectAsync(gateway.CallAsync(GatewayHarness.Request(), _ct), _ct);

        var published = Assert.IsType<AgentCapacityChanged>(Assert.Single(h.Sink.Events));
        Assert.Equal(GatewayHarness.Agent, published.AgentId);
        Assert.False(published.Available);
    }
}
