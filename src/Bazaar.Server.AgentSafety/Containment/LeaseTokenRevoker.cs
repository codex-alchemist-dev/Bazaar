using Bazaar.Server.Identity.Leases;
using Bazaar.Server.Security.KillSwitch;
using Bazaar.Server.Security.Quarantine;

namespace Bazaar.Server.AgentSafety.Containment;

/// <summary>
/// The real <see cref="IAgentTokenRevoker"/>: revokes every active credential lease the agent holds, through the Identity lease store.
/// </summary>
/// <remarks>
/// Revocation is permanent. Resume reports not reversible, and new tokens come from a separate issuance flow. The durability of the
/// revocation is the lease store's: on main the Identity lease store is in-memory only, so a restart clears leases with them.
/// </remarks>
public sealed class LeaseTokenRevoker(ICredentialLeaseStore leases) : IAgentTokenRevoker
{
    /// <inheritdoc />
    public Task RevokeAsync(string agentId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);

        // One spelling per agent: the lease store compares ids as plain strings, so a stop recorded as an upper-case or braced GUID
        // would otherwise revoke nothing.
        return leases.RevokeAgentAsync(AgentIds.Canonical(agentId), ct);
    }

    /// <inheritdoc />
    public Task<TokenResumeResult> ResumeAsync(string agentId, CancellationToken ct = default) =>
        Task.FromResult(TokenResumeResult.NotReversible);
}
