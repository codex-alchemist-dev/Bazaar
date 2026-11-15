namespace Bazaar.Server.Security.KillSwitch;

/// <summary>
/// Host hook for a per-agent kill switch (doc 15: revoke leases, detach mounts, block egress, pause both VMs). The engine
/// calls it after it has set the stop; the host implements the VM and lease work. Both methods must be idempotent.
/// </summary>
public interface IAgentContainment
{
    /// <summary>Pauses the agent's VMs, revokes its leases and tokens, and blocks its egress. Called when an agent stop is engaged.</summary>
    Task ContainAsync(string agentId, CancellationToken ct = default);

    /// <summary>
    /// Lifts the host-side containment after an agent stop is resumed. Only the owner may release: the implementation
    /// validates <paramref name="ownerActorId"/> itself (the engine does not authorize) and audits every attempt. Leases and
    /// tokens are restored only where the token revoker can; otherwise the result says so.
    /// </summary>
    /// <param name="agentId">The contained agent.</param>
    /// <param name="ownerActorId">The owner's registered device id, as known to the owner key registry.</param>
    /// <param name="ct">Cancellation.</param>
    Task<ContainmentReleaseResult> ReleaseAsync(string agentId, string ownerActorId, CancellationToken ct = default);
}

/// <summary>Outcome of <see cref="IAgentContainment.ReleaseAsync"/>.</summary>
public enum ContainmentReleaseOutcome
{
    /// <summary>The VMs were resumed and the token resume ran.</summary>
    Released,

    /// <summary>The caller is not the owner. Nothing was resumed.</summary>
    Denied,

    /// <summary>The agent was not contained by this host. Nothing was resumed.</summary>
    NotContained,
}

/// <summary>Result of a release request.</summary>
/// <param name="Outcome">What happened.</param>
/// <param name="TokensRestored">True only when the token revoker restored the agent's tokens.</param>
public sealed record ContainmentReleaseResult(ContainmentReleaseOutcome Outcome, bool TokensRestored = false);
