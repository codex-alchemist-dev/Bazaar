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

    /// <summary>Changes the state of a registered agent. Throws when the agent is unknown.</summary>
    Task<AgentRecord> SetStateAsync(AgentId id, AgentState state, CancellationToken ct);
}
