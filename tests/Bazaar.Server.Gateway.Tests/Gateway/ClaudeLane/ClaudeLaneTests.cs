using Bazaar.Contracts.Gateway;
using Bazaar.Server.Gateway.AnthropicDirect;
using Bazaar.Server.Gateway.ClaudeLane;
using Bazaar.Server.Gateway.Data;
using Bazaar.Server.Gateway.FreeLlmApi;
using Bazaar.Server.Gateway.Policy;
using Bazaar.Server.Gateway.Selection;
using Bazaar.Server.Tests.Gateway.Pipeline;

namespace Bazaar.Server.Tests.Gateway.ClaudeLane;

/// <summary>The Claude lane route (doc 05, doc 92): Anthropic key first, FreeLLMAPI behind it, never leaving the Claude family.</summary>
public sealed class ClaudeLaneTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class EmptyDirectory : IGatewayDirectory
    {
        public ValueTask<GwProvider?> GetProviderAsync(string providerId, CancellationToken cancellationToken) => ValueTask.FromResult<GwProvider?>(null);

        public ValueTask<GwModel?> GetModelAsync(ModelRef model, CancellationToken cancellationToken) => ValueTask.FromResult<GwModel?>(null);

        public ValueTask<GwRoute?> GetRouteAsync(string routeId, CancellationToken cancellationToken) => ValueTask.FromResult<GwRoute?>(null);
    }

    private static AnthropicDirectOptions Anthropic(bool on = true) => new() { Enabled = on, ApiKey = "env:K", Models = ["claude-haiku-5-5"] };

    private static FreeLlmApiOptions FreeLlm(bool on = true) => new() { Enabled = on };

    private static ClaudeLaneGatewayDirectory Lane(ClaudeLaneOptions lane, AnthropicDirectOptions? a = null, FreeLlmApiOptions? f = null) =>
        new(new EmptyDirectory(), lane, a ?? Anthropic(), f ?? FreeLlm(), TimeProvider.System);

    [Fact]
    public async Task The_route_holds_native_models_first_then_pooled_ones_all_in_the_claude_family()
    {
        var directory = Lane(new ClaudeLaneOptions { Enabled = true, PooledModels = ["claude-haiku-5-5", "claude-sonnet-5-5"] });

        var route = await directory.GetRouteAsync("claude", Ct);

        Assert.NotNull(route);
        Assert.Equal(
            ["anthropic-direct:claude-haiku-5-5", "freellmapi:claude-haiku-5-5", "freellmapi:claude-sonnet-5-5"],
            route.Members.Select(m => m.ToString()));
        var pooled = await directory.GetModelAsync(new ModelRef("freellmapi", "claude-sonnet-5-5"), Ct);
        Assert.Equal(("claude", GatewayIdentity.Pooled), (pooled!.Family, pooled.Identity));
    }

    [Fact]
    public async Task A_source_that_is_off_adds_no_member()
    {
        var onlyAnthropic = Lane(new ClaudeLaneOptions { Enabled = true, PooledModels = ["claude-haiku-5-5"] }, f: FreeLlm(on: false));
        Assert.Equal(["anthropic-direct:claude-haiku-5-5"], onlyAnthropic.Members.Select(m => m.ToString()));

        var neither = Lane(new ClaudeLaneOptions { Enabled = true }, a: Anthropic(on: false), f: FreeLlm(on: false));
        Assert.Null(await neither.GetRouteAsync("claude", Ct));
    }

    [Fact]
    public async Task Other_lookups_fall_through_to_the_inner_directory()
    {
        var directory = Lane(new ClaudeLaneOptions { Enabled = true });
        Assert.Null(await directory.GetRouteAsync("something-else", Ct));
        Assert.Null(await directory.GetModelAsync(new ModelRef("a", "m"), Ct));
    }

    [Fact]
    public void Validation_refuses_bad_route_names_and_pooled_ids()
    {
        var validator = new ClaudeLaneOptionsValidator();
        Assert.True(validator.Validate(null, new ClaudeLaneOptions()).Succeeded);
        Assert.True(validator.Validate(null, new ClaudeLaneOptions { Enabled = true }).Succeeded);
        Assert.False(validator.Validate(null, new ClaudeLaneOptions { Enabled = true, RouteName = "auto/claude" }).Succeeded);
        Assert.False(validator.Validate(null, new ClaudeLaneOptions { Enabled = true, RouteName = "custom/claude" }).Succeeded);
        Assert.False(validator.Validate(null, new ClaudeLaneOptions { Enabled = true, PooledModels = ["bad id*"] }).Succeeded);
    }

    [Fact]
    public async Task Gateway_serves_pooled_only_when_the_native_endpoint_is_blocked()
    {
        var h = new GatewayHarness();
        h.World.Models[new ModelRef("b", "m")] = h.World.Models[new ModelRef("b", "m")] with { Identity = GatewayIdentity.Pooled };
        var stats = new EndpointStatsTracker(time: h.Clock);
        for (var i = 0; i < 8; i++)
        {
            stats.Begin(new EndpointKey("a", "m")).Complete(new AttemptOutcome(AttemptOutcomeKind.Success, TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(4)));
            stats.Begin(new EndpointKey("b", "m")).Complete(new AttemptOutcome(AttemptOutcomeKind.Success, TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(100)));
        }

        var ranker = new ProfileBoundRanker(new EndpointSelector(stats, time: h.Clock));
        await ranker.BindAsync("r", new SelectionBinding("latency", IdentityTiers: true), Ct);

        h.Adapter("a").Provider.EnqueueCall(GatewayHarness.Reply("native"));
        await GatewayHarness.CollectAsync(h.Gateway(ranker: ranker, stats: stats).CallAsync(GatewayHarness.Request(), Ct), Ct);
        Assert.Equal("a", h.Usage.Records[^1].ProviderId);

        h.Quota.Blocked[(GatewayHarness.KeyOf("a"), "claude")] = (QuotaState.SpendCapped, h.Clock.GetUtcNow().AddDays(1));
        h.Adapter("b").Provider.EnqueueCall(GatewayHarness.Reply("pooled"));
        await GatewayHarness.CollectAsync(h.Gateway(ranker: ranker, stats: stats).CallAsync(GatewayHarness.Request(), Ct), Ct);
        var row = h.Usage.Records[^1];
        Assert.Equal(("b", GatewayIdentity.Pooled), (row.ProviderId, row.Identity));
    }
}
