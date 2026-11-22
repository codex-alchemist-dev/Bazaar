namespace Bazaar.Server.Security.KillSwitch;

/// <summary>
/// Host hook for a per-agent kill switch (doc 15: revoke leases, detach mounts, block egress, pause both VMs). The engine
/// calls it after it has set the stop; the host implements the VM and lease work. Both methods must be idempotent.
/// </summary>
public interface IAgentContainment
{
    /// <summary>
    /// Pauses the agent's VMs, revokes its leases and tokens, and blocks its egress. Called when an agent stop is engaged.
    /// <paramref name="actorId"/> is the engaging actor (an owner device, or the breach actor for an automatic stop), recorded with the containment.
    /// </summary>
    Task ContainAsync(string agentId, string actorId, CancellationToken ct = default);

    /// <summary>
    /// Issues a single-use release challenge for an agent to a registered, unrevoked owner device. The server generates the
    /// nonce; the device signs it (see <see cref="ContainmentReleaseRequest"/>). Returns null, and audits a denial, when the
    /// device is not a registered owner device.
    /// </summary>
    Task<ReleaseChallenge?> IssueReleaseChallengeAsync(string agentId, string deviceId, CancellationToken ct = default);

    /// <summary>
    /// Lifts the host-side containment after an agent stop is resumed. Only a signed owner request releases: the
    /// implementation verifies the device key, the signature, and the single-use nonce itself (the engine does not
    /// authorize) and audits every attempt. Leases and tokens are restored only where the token revoker can; otherwise the
    /// result says so.
    /// </summary>
    /// <param name="agentId">The contained agent.</param>
    /// <param name="request">The request signed by the owner device over a nonce from <see cref="IssueReleaseChallengeAsync"/>.</param>
    /// <param name="ct">Cancellation.</param>
    Task<ContainmentReleaseResult> ReleaseAsync(string agentId, ContainmentReleaseRequest request, CancellationToken ct = default);
}

/// <summary>A release challenge: the nonce the owner device must sign, and when it stops being valid.</summary>
/// <param name="Nonce">128-bit random nonce, single use.</param>
/// <param name="ExpiresAt">Expiry the device must include in what it signs.</param>
public sealed record ReleaseChallenge(byte[] Nonce, DateTimeOffset ExpiresAt);

/// <summary>
/// A signed request to release one agent. The signature covers
/// <see cref="Approvals.ApprovalSignature.ReleaseMessage"/> over (agent id, device id, nonce, expiry), made with the owner
/// device key (ECDSA P-256, SHA-256, P1363).
/// </summary>
/// <param name="AgentId">The agent to release.</param>
/// <param name="DeviceId">The owner device that signed.</param>
/// <param name="Nonce">The nonce from the challenge.</param>
/// <param name="ExpiresAt">The expiry from the challenge.</param>
/// <param name="Signature">64-byte signature over the canonical message.</param>
public sealed record ContainmentReleaseRequest(string AgentId, string DeviceId, byte[] Nonce, DateTimeOffset ExpiresAt, byte[] Signature);

/// <summary>Outcome of <see cref="IAgentContainment.ReleaseAsync"/>.</summary>
public enum ContainmentReleaseOutcome
{
    /// <summary>The VMs were resumed and the token resume ran.</summary>
    Released,

    /// <summary>The request was not a valid signed owner request. Nothing was resumed.</summary>
    Denied,

    /// <summary>The agent was not contained by this host. Nothing was resumed.</summary>
    NotContained,
}

/// <summary>Result of a release request.</summary>
/// <param name="Outcome">What happened.</param>
/// <param name="TokensRestored">True only when the token revoker restored the agent's tokens.</param>
public sealed record ContainmentReleaseResult(ContainmentReleaseOutcome Outcome, bool TokensRestored = false);
