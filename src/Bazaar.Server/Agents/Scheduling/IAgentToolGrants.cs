namespace Bazaar.Server.Agents.Scheduling;

/// <summary>
/// The tools one agent is granted (doc 06, "Created"; doc 15 capability classes). A woken turn is offered only the catalogue
/// entries its agent's grants name, so the grant is the agent's, never the operator's. An agent with no grants gets no tools.
/// </summary>
public interface IAgentToolGrants
{
    /// <summary>The names of the tools the agent is granted now, ordered by name. Revoked grants are not returned.</summary>
    Task<IReadOnlyList<string>> GrantedToolsAsync(Guid agentId, CancellationToken cancellationToken);
}
