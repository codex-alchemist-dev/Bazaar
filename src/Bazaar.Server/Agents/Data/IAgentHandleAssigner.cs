namespace Bazaar.Server.Agents.Data;

/// <summary>
/// Gives a new agent its default mention handle. A seam so <see cref="AgentRepository"/> does not depend on the handle store;
/// the production implementation is <c>PrincipalHandleRepository</c> (<c>mentions/0001</c>).
/// </summary>
public interface IAgentHandleAssigner
{
    /// <summary>
    /// Makes sure the agent has a handle and returns it, or null when <paramref name="name"/> gives no usable handle. An agent
    /// that already has a handle keeps it, so calling this again is safe.
    /// </summary>
    Task<string?> EnsureAgentHandleAsync(Guid agentId, string name, CancellationToken ct = default);
}
