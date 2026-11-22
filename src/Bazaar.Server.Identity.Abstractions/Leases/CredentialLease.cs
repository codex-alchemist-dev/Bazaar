using Bazaar.Server.Identity;

namespace Bazaar.Server.Identity.Leases;

/// <summary>
/// An issued credential lease (doc 14, <c>credential_lease</c>). Holds ids and timing only, never secret material.
/// </summary>
/// <param name="Id">Lease id.</param>
/// <param name="AgentId">The one agent the lease was issued to.</param>
/// <param name="CredentialId">The credential the lease refers to.</param>
/// <param name="Scope">What the lease permits, for example <c>service:action</c>.</param>
/// <param name="IssuedAt">When the lease was issued.</param>
/// <param name="ExpiresAt">When the lease stops being valid.</param>
/// <param name="RevokedAt">When the lease was revoked, or null.</param>
/// <param name="ApprovalId">The owner approval that backed the lease, or null for tiers that need none.</param>
public sealed record CredentialLease(
    LeaseId Id,
    string AgentId,
    CredentialId CredentialId,
    string Scope,
    DateTimeOffset IssuedAt,
    DateTimeOffset ExpiresAt,
    DateTimeOffset? RevokedAt,
    Guid? ApprovalId)
{
    /// <summary>True when the lease is not revoked and <paramref name="now"/> is before its expiry.</summary>
    public bool IsActive(DateTimeOffset now) => RevokedAt is null && now < ExpiresAt;
}
