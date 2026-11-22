namespace Bazaar.Secrets;

/// <summary>Lifecycle of an agent identity. Only <see cref="AgentState.Active"/> agents can have secrets read by a host.</summary>
public enum AgentState
{
    Active,
    Quarantined,
    Revoked,
}

public sealed record AgentRecord(AgentId Id, AgentState State, DateTimeOffset UpdatedAt);

/// <summary>Agent identities and their lifecycle. The kill switch and quarantine act here.</summary>
public interface IAgentRegistry
{
    /// <summary>Creates the agent as Active. Idempotent: an existing agent is returned unchanged.</summary>
    Task<AgentRecord> RegisterAsync(AgentId id, CancellationToken ct);

    Task<AgentRecord?> GetAsync(AgentId id, CancellationToken ct);

    /// <summary>
    /// Moves a registered agent to <paramref name="target"/> under <see cref="AgentTransitionRules"/>. Throws
    /// <see cref="AgentTransitionException"/> for a refused change and <see cref="InvalidOperationException"/> for an unknown agent.
    /// Same-state requests return the current record and change nothing. Every real transition is audited.
    /// </summary>
    Task<AgentRecord> TransitionAsync(AgentId id, AgentState target, OwnerRelease? release, CancellationToken ct);
}
