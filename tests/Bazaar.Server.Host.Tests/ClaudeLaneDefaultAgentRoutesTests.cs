using Bazaar.Server.Gateway.ClaudeLane;
using Bazaar.Server.Gateway.Data;
using Bazaar.Server.Gateway.Policy;
using Bazaar.Server.Gateway.Selection;
using Bazaar.Server.Host.Composition;
using Microsoft.Extensions.Options;

namespace Bazaar.Server.Host.Tests;

public class ClaudeLaneDefaultAgentRoutesTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class Directory(GwRoute? route) : IGatewayDirectory
    {
        public ValueTask<GwProvider?> GetProviderAsync(string providerId, CancellationToken cancellationToken) => ValueTask.FromResult<GwProvider?>(null);

        public ValueTask<GwModel?> GetModelAsync(ModelRef model, CancellationToken cancellationToken) => ValueTask.FromResult<GwModel?>(null);

        public ValueTask<GwRoute?> GetRouteAsync(string routeId, CancellationToken cancellationToken) =>
            ValueTask.FromResult(route is not null && route.Id == routeId ? route : null);
    }

    private sealed class Setup : IAgentSelectionSetup
    {
        public List<(string Route, SelectionBinding Binding)> Bound { get; } = [];

        public Task<IReadOnlyList<string>> ListProfilesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>(["balanced", "latency"]);

        public Task BindAsync(string routeId, string profileName, CancellationToken cancellationToken = default) =>
            BindAsync(routeId, new SelectionBinding(profileName), cancellationToken);

        public Task BindAsync(string routeId, SelectionBinding binding, CancellationToken cancellationToken = default)
        {
            Bound.Add((routeId, binding));
            return Task.CompletedTask;
        }
    }

    private static GwRoute Route() => new("claude", RouteStrategy.Priority,
        [new ModelRef("anthropic-direct", "claude-haiku-5-5"), new ModelRef("freellmapi", "claude-haiku-5-5")], []);

    private static ClaudeLaneDefaultAgentRoutes Routes(GwRoute? route, Setup setup) =>
        new(new Directory(route), setup, Options.Create(new ClaudeLaneOptions { Enabled = true }));

    [Fact]
    public async Task Every_lane_member_is_listed_and_the_chosen_profile_is_bound()
    {
        var setup = new Setup();
        var whitelist = await Routes(Route(), setup).GetWhitelistAsync([], "latency", Ct);

        Assert.Equal(["anthropic-direct:claude-haiku-5-5", "freellmapi:claude-haiku-5-5"], whitelist);
        var bound = Assert.Single(setup.Bound);
        Assert.Equal("claude", bound.Route);
        Assert.Equal("latency", bound.Binding.Profile);
        Assert.True(bound.Binding.IdentityTiers);
    }

    [Fact]
    public async Task Requested_sources_narrow_the_list_and_unknown_ones_add_nothing()
    {
        var setup = new Setup();
        var routes = Routes(Route(), setup);

        Assert.Equal(["freellmapi:claude-haiku-5-5"], await routes.GetWhitelistAsync(["freellmapi"], "balanced", Ct));
        Assert.Empty(await routes.GetWhitelistAsync(["nope"], "balanced", Ct));
        Assert.Single(setup.Bound);
    }

    [Fact]
    public async Task An_unknown_profile_falls_back_to_the_lanes_own_and_no_route_gives_an_empty_list()
    {
        var setup = new Setup();
        await Routes(Route(), setup).GetWhitelistAsync([], "made-up", Ct);
        Assert.Equal("balanced", Assert.Single(setup.Bound).Binding.Profile);

        var none = new Setup();
        Assert.Empty(await Routes(null, none).GetWhitelistAsync([], "balanced", Ct));
        Assert.Empty(none.Bound);
    }
}
