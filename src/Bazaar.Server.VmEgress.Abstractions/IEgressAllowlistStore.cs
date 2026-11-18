namespace Bazaar.Server.VmEgress;

/// <summary>
/// Durable per-agent allowlist, holding what the owner has approved. The policy compares every new rule set against it to decide
/// whether approval is needed, so an implementation must return exactly what was last saved.
/// </summary>
public interface IEgressAllowlistStore
{
    /// <summary>The agent's approved rules, or null when none were ever set.</summary>
    Task<EgressRules?> GetAsync(string agentId, CancellationToken ct = default);

    /// <summary>Replaces the agent's approved rules.</summary>
    Task SaveAsync(string agentId, EgressRules rules, CancellationToken ct = default);

    /// <summary>Whether the kill switch has blocked this agent's egress. Survives a restart, so the host stays closed.</summary>
    Task<bool> IsBlockedAsync(string agentId, CancellationToken ct = default);

    /// <summary>Records the blocked flag.</summary>
    Task SetBlockedAsync(string agentId, bool blocked, CancellationToken ct = default);
}
