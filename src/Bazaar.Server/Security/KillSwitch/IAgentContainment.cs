namespace Bazaar.Server.Security.KillSwitch;

/// <summary>
/// Host hook for a per-agent kill switch (doc 15: revoke leases, detach mounts, block egress, pause both VMs). The engine
/// calls it after it has set the stop; the host implements the VM and lease work. Both methods must be idempotent.
/// </summary>
public interface IAgentContainment
{
    /// <summary>Pauses the agent's VMs, revokes its leases and tokens, and blocks its egress. Called when an agent stop is engaged.</summary>
    Task ContainAsync(string agentId, CancellationToken ct = default);

    /// <summary>Lifts the host-side containment after an agent stop is resumed. Leases are not restored by this call.</summary>
    Task ReleaseAsync(string agentId, CancellationToken ct = default);
}
