using Bazaar.Contracts.Gateway;
using Bazaar.Server.Agents.Data;
using Bazaar.Server.Gateway.AnthropicDirect;
using Bazaar.Server.Gateway.Data;
using Bazaar.Server.Gateway.Policy;
using Bazaar.Server.Setup.FirstRun.Gateway;

namespace Bazaar.Server.Host.Composition;

/// <summary>
/// Backs the first-run seed's gateway ports with the gateway tables (doc 45 section 1.15): Claude-family models of enabled
/// providers, the default agent's priority route, and its <c>agent_model_policy</c> row.
/// </summary>
public sealed class GatewayClaudeSourceDirectory(GatewayDirectoryRepository directory) : IClaudeSourceDirectory
{
    /// <inheritdoc />
    public async Task<IReadOnlyList<ClaudeSourceModel>> ListClaudeModelsAsync(CancellationToken cancellationToken = default)
    {
        var result = new List<ClaudeSourceModel>();
        foreach (var provider in await directory.ListProvidersAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!provider.Enabled) continue;
            foreach (var model in await directory.ListModelsAsync(provider.Id, cancellationToken).ConfigureAwait(false))
            {
                if (model.Status != GwModel.StatusAvailable) continue;
                if (!string.Equals(model.Family, AnthropicDirectProvider.Family, StringComparison.Ordinal)) continue;
                result.Add(new ClaudeSourceModel(model.Ref, model.Identity == GatewayIdentity.Native));
            }
        }

        return result;
    }

    /// <inheritdoc />
    public Task UpsertPriorityRouteAsync(string routeId, IReadOnlyList<ModelRef> members, CancellationToken cancellationToken = default) =>
        directory.UpsertRouteAsync(new GwRoute(routeId, RouteStrategy.Priority, members, []), cancellationToken);
}

/// <summary>The seeded agent's policy row, written through the gateway's policy repository so every entry is validated.</summary>
public sealed class GatewayDefaultAgentPolicyStore(AgentModelPolicyRepository policies) : IDefaultAgentPolicyStore
{
    /// <inheritdoc />
    public async Task<string?> GetChangedByAsync(string agentId, CancellationToken cancellationToken = default) =>
        (await policies.GetRowAsync(agentId, cancellationToken).ConfigureAwait(false))?.ChangedBy;

    /// <inheritdoc />
    public Task SaveAsync(string agentId, IReadOnlyList<string> entries, string? defaultRoute, string changedBy, CancellationToken cancellationToken = default) =>
        policies.SaveAsync(agentId, entries, defaultRoute, changedBy, cancellationToken);
}

/// <summary>Binds the Agents module's grant port to the default-policy grant of the first-run seed (doc 45 section 1.15).</summary>
public sealed class AgentDefaultPolicyGrantAdapter(DefaultAgentGrantService grants) : IAgentDefaultPolicyGrant
{
    /// <inheritdoc />
    public async Task GrantAsync(Guid agentId, CancellationToken ct) =>
        await grants.GrantAsync(agentId, ct).ConfigureAwait(false);
}

/// <summary>Tells the repair pass which agents have no policy row.</summary>
public sealed class AgentPolicyPresenceAdapter(DefaultAgentGrantService grants) : IAgentPolicyPresence
{
    /// <inheritdoc />
    public Task<IReadOnlyList<Guid>> FilterWithoutPolicyAsync(IEnumerable<Guid> agentIds, CancellationToken ct) =>
        grants.FilterWithoutPolicyAsync(agentIds, ct);
}
