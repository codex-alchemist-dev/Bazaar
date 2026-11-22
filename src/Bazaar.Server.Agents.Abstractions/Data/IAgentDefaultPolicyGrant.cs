namespace Bazaar.Server.Agents.Data;

/// <summary>
/// Port for granting a newly seeded agent its default model policy (doc 45, section 1.15). The first-run seeds call it once,
/// right after they create a new agent, and never when a marker match returns an existing one. The composition root binds the
/// concrete grant. The seeds default to <see cref="NoAgentDefaultPolicyGrant"/>, which does nothing.
/// </summary>
/// <remarks>
/// Implementations must be idempotent. A failed grant leaves the agent without a policy row, and the repair pass
/// (<see cref="AgentGrantRepair"/>) calls the grant again for every such agent on the next start.
/// </remarks>
public interface IAgentDefaultPolicyGrant
{
    /// <summary>Grants the default policy to the agent. Implementations must not overwrite a policy an operator has edited.</summary>
    Task GrantAsync(Guid agentId, CancellationToken ct);
}

/// <summary>
/// Port that tells which agents have no policy row, so a missing default grant can be repaired (doc 45, section 1.15).
/// The policy owner implements it. When no implementation is registered, the repair pass repairs nothing.
/// </summary>
public interface IAgentPolicyPresence
{
    /// <summary>Returns the ids, from <paramref name="agentIds"/>, of the agents that have no policy row.</summary>
    Task<IReadOnlyList<Guid>> FilterWithoutPolicyAsync(IEnumerable<Guid> agentIds, CancellationToken ct);
}
