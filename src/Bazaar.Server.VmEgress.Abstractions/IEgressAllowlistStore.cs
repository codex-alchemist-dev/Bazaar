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

    /// <summary>The agent's block epoch: 0 until the first block, then raised by every <see cref="BlockAsync"/>.</summary>
    Task<long> GetBlockEpochAsync(string agentId, CancellationToken ct = default);

    /// <summary>Sets the blocked flag and raises the epoch, in one atomic step. Returns the new epoch. Safe to repeat; each call raises it.</summary>
    Task<long> BlockAsync(string agentId, CancellationToken ct = default);

    /// <summary>
    /// Clears the blocked flag only if the epoch is still <paramref name="expectedEpoch"/>, atomically. Returns false when another
    /// block got in first (or the flag was already clear); the block then stays. This is how a release cannot undo a newer block.
    /// </summary>
    Task<bool> TryReleaseBlockAsync(string agentId, long expectedEpoch, CancellationToken ct = default);
}
