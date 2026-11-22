using Bazaar.Server.Setup.FirstRun;

namespace Bazaar.Server.Setup.FirstRun.Gateway;

/// <summary>Which sources and selection profile the default agent's whitelist is built from (doc 45 section 1.15 plan keys).</summary>
public sealed class DefaultAgentGrantOptions
{
    /// <summary>Source ids that narrow the whitelist; empty means every Claude source the gateway has.</summary>
    public IReadOnlyList<string> Sources { get; init; } = [];

    /// <summary>The selection profile bound to the route (doc 92).</summary>
    public string Profile { get; init; } = "balanced";
}

/// <summary>
/// Gives a newly created agent its default model policy: builds the whitelist from the gateway and writes the policy row.
/// Idempotent (a row an operator edited is left alone, an own row is refreshed), so a startup retry for agents that have no
/// policy row is safe. The Agents module's port calls <see cref="GrantAsync"/>; Agents never references this module.
/// </summary>
public sealed class DefaultAgentGrantService(
    IDefaultAgentRoutes routes,
    DefaultAgentPolicyGrant grant,
    IDefaultAgentPolicyStore store,
    DefaultAgentGrantOptions? options = null)
{
    private readonly DefaultAgentGrantOptions _options = options ?? new DefaultAgentGrantOptions();

    /// <summary>Builds the whitelist and writes the policy. False when nothing was written (no Claude source, or an operator edit).</summary>
    public async Task<bool> GrantAsync(Guid agentId, CancellationToken cancellationToken = default)
    {
        var whitelist = await routes.GetWhitelistAsync(_options.Sources, _options.Profile, cancellationToken);
        return await grant.GrantAsync(agentId, whitelist, cancellationToken);
    }

    /// <summary>The ids among <paramref name="agentIds"/> that have no policy row yet, for the startup retry.</summary>
    public async Task<IReadOnlyList<Guid>> FilterWithoutPolicyAsync(IEnumerable<Guid> agentIds, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(agentIds);
        var missing = new List<Guid>();
        foreach (var id in agentIds)
        {
            if (await store.GetChangedByAsync(id.ToString("D"), cancellationToken) is null) missing.Add(id);
        }

        return missing;
    }
}
