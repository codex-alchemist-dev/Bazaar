namespace Bazaar.Server.Identity.Leases;

/// <summary>
/// Hears lease lifecycle events from <see cref="IIdentityService"/> (doc 14, "Access flow"). Identity holds lease ids only, never secret
/// values, so an observer that needs the value resolves it itself from the <see cref="CredentialLease.CredentialId"/> (for example the
/// leased-credential matcher, which resolves it through <c>ISecretStore</c> host-side). Observers run after the lease change is stored
/// and after its audit entry is written. A throwing observer is logged and never changes an access decision or a revocation.
/// </summary>
public interface ILeaseObserver
{
    /// <summary>A lease was issued. The lease carries agent, credential, scope and expiry.</summary>
    Task OnIssuedAsync(CredentialLease lease, CancellationToken ct);

    /// <summary>
    /// Leases in <paramref name="scope"/> were revoked: one agent's leases (an explicit revoke or a quarantined agent's request) or all of them.
    /// </summary>
    Task OnRevokedAsync(RevocationScope scope, CancellationToken ct);
}
