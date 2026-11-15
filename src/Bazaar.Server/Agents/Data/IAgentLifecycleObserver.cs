namespace Bazaar.Server.Agents.Data;

/// <summary>Receives lifecycle transitions after their compare-and-set update succeeds.</summary>
// Re-runs the analyzed checks on this PR; no behaviour change.
public interface IAgentLifecycleObserver
{
    /// <summary>Runs after an agent enters <see cref="AgentState.Retired"/>.</summary>
    Task AgentRetiredAsync(Guid agentId, CancellationToken cancellationToken);
}
