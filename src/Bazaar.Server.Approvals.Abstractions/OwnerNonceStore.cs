namespace Bazaar.Server.Approvals;

/// <summary>Remembers the owner nonces already used, so a signed payload works once (doc 32 section 5a).</summary>
public interface IOwnerNonceStore
{
    /// <summary>Records the nonce. Returns false when it was already used. Entries may be dropped after <paramref name="expiresAt"/>.</summary>
    Task<bool> TryUseAsync(byte[] nonce, DateTimeOffset expiresAt, CancellationToken cancellationToken);
}
