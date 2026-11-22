namespace Bazaar.Server.Agents.Data;

/// <summary>
/// Port for granting a newly seeded agent its default model policy (doc 45, section 1.15). The first-run seeds call it once,
/// right after they create a new agent, and never when a marker match returns an existing one. The composition root binds the
/// concrete grant. The seeds default to <see cref="NoAgentDefaultPolicyGrant"/>, which does nothing.
/// </summary>
public interface IAgentDefaultPolicyGrant
{
    /// <summary>Grants the default policy to the agent. Implementations must not overwrite a policy an operator has edited.</summary>
    Task GrantAsync(Guid agentId, CancellationToken ct);
}
