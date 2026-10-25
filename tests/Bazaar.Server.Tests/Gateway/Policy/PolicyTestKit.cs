using Bazaar.Contracts.Gateway;
using Bazaar.Server.Gateway.Data;
using Bazaar.Server.Gateway.Policy;

namespace Bazaar.Server.Tests.Gateway.Policy;

internal sealed class InMemoryPolicyWorld : IAgentPolicyStore, IGatewayDirectory
{
    public Dictionary<string, AgentModelPolicy> Policies { get; } = [];

    public Dictionary<string, GwProvider> Providers { get; } = [];

    public Dictionary<ModelRef, GwModel> Models { get; } = [];

    public Dictionary<string, GwRoute> Routes { get; } = [];

    public ValueTask<AgentModelPolicy?> GetAsync(string agentId, CancellationToken cancellationToken) =>
        ValueTask.FromResult(Policies.GetValueOrDefault(agentId));

    public ValueTask<GwProvider?> GetProviderAsync(string providerId, CancellationToken cancellationToken) =>
        ValueTask.FromResult(Providers.GetValueOrDefault(providerId));

    public ValueTask<GwModel?> GetModelAsync(ModelRef model, CancellationToken cancellationToken) =>
        ValueTask.FromResult(Models.GetValueOrDefault(model));

    public ValueTask<GwRoute?> GetRouteAsync(string routeId, CancellationToken cancellationToken) =>
        ValueTask.FromResult(Routes.GetValueOrDefault(routeId));

    public InMemoryPolicyWorld AddProvider(string id, string authKind = "api_key", bool enabled = true, string? secretRef = "secret://bazaar/providers/x/key")
    {
        Providers[id] = new GwProvider(id, "first_party", "openai_chat", "https://example.invalid/v1", authKind, authKind == "none" ? null : secretRef, "{}", null, null, enabled);
        return this;
    }

    public InMemoryPolicyWorld AddModel(string provider, string model, string family = "claude", string status = GwModel.StatusAvailable)
    {
        var at = DateTimeOffset.UnixEpoch;
        Models[new ModelRef(provider, model)] = new GwModel(provider, model, family, GatewayIdentity.Native, "{}", status, at, at, "test");
        return this;
    }

    public InMemoryPolicyWorld AddRoute(string id, IEnumerable<string> members, IEnumerable<string>? expected = null)
    {
        Routes[id] = new GwRoute(id, RouteStrategy.Priority, [.. members.Select(ModelRef.Parse)], [.. expected ?? []]);
        return this;
    }

    public InMemoryPolicyWorld AddPolicy(string agent, string[] entries, string? defaultRoute = null)
    {
        Policies[agent] = AgentModelPolicy.Create(agent, entries, defaultRoute, 1);
        return this;
    }

    public RouteResolver Resolver(PolicyTightening? tightening = null) => new(this, this, tightening);

    public static ModelRequest Request(string agent, string route, bool tools = false) =>
        new(agent, route, [new ModelMessage(ModelRole.User, "hi")], tools ? [new ModelToolDefinition("t", "d", "{}")] : []);
}
