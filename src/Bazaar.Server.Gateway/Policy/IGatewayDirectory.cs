using Bazaar.Server.Gateway.Data;

namespace Bazaar.Server.Gateway.Policy;

/// <summary>Reads one agent's policy. Returns null for an agent that has none, which the resolver treats as denied.</summary>
public interface IAgentPolicyStore
{
    ValueTask<AgentModelPolicy?> GetAsync(string agentId, CancellationToken cancellationToken);
}

/// <summary>Read side of the provider, model and route tables that the resolver needs.</summary>
public interface IGatewayDirectory
{
    ValueTask<GwProvider?> GetProviderAsync(string providerId, CancellationToken cancellationToken);

    ValueTask<GwModel?> GetModelAsync(ModelRef model, CancellationToken cancellationToken);

    ValueTask<GwRoute?> GetRouteAsync(string routeId, CancellationToken cancellationToken);
}
