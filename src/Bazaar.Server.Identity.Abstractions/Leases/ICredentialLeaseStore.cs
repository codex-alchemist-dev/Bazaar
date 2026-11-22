using Bazaar.Server.Identity;

namespace Bazaar.Server.Identity.Leases;

/// <summary>
/// Store for credential leases (doc 14, <c>credential_lease</c>). Leases are bound to one agent: a lookup under any
/// other agent id returns nothing. An approval id may back at most one lease (doc 14, "Replay, restart, quarantine").
/// </summary>
public interface ICredentialLeaseStore
{
    /// <summary>
    /// Records a new lease. Returns null, and records nothing, when <paramref name="approvalId"/> already backs a lease.
    /// </summary>
    Task<CredentialLease?> IssueAsync(
        LeaseId id,
        string agentId,
        CredentialId credentialId,
        string scope,
        DateTimeOffset expiresAt,
        Guid? approvalId,
        CancellationToken ct = default);

    /// <summary>Returns the lease when it exists, belongs to <paramref name="agentId"/>, is not revoked and has not expired.</summary>
    Task<CredentialLease?> GetAsync(LeaseId id, string agentId, CancellationToken ct = default);

    /// <summary>Lists the agent's leases that are still active.</summary>
    Task<IReadOnlyList<CredentialLease>> ListActiveAsync(string agentId, CancellationToken ct = default);

    /// <summary>True when a lease has already been issued for this approval id.</summary>
    Task<bool> IsApprovalUsedAsync(Guid approvalId, CancellationToken ct = default);

    /// <summary>Revokes the leases in scope: one agent's, or every agent's for a global revocation.</summary>
    Task RevokeAsync(RevocationScope scope, CancellationToken ct = default);

    /// <summary>Revokes every active lease of one agent.</summary>
    Task RevokeAgentAsync(string agentId, CancellationToken ct = default);
}
