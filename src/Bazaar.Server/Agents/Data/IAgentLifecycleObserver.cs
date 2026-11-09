namespace Bazaar.Server.Agents.Data;

/// <summary>Receives lifecycle transitions after their compare-and-set update succeeds.</summary>
public interface IAgentLifecycleObserver
{
    /// <summary>Runs after an agent enters <see cref="AgentState.Retired"/>.</summary>
    Task AgentRetiredAsync(Guid agentId, CancellationToken cancellationToken);
}
