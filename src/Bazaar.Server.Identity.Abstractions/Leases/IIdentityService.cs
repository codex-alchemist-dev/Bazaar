using Bazaar.Server.Identity;

namespace Bazaar.Server.Identity.Leases;

/// <summary>
/// The Identity Service entry point for credential access (doc 14, "Access flow"). It is the only host-side component
/// that decides whether an agent may use a credential.
/// </summary>
public interface IIdentityService
{
    /// <summary>Decides one access request and, when allowed, issues a lease after the audit event is written.</summary>
    Task<AccessDecision> RequestAccessAsync(AccessRequest request, CancellationToken ct = default);

    /// <summary>Revokes leases in scope. Revocation is immediate for leases (doc 14, "Behaviour and edge cases").</summary>
    Task RevokeAsync(RevocationScope scope, CancellationToken ct = default);
}
