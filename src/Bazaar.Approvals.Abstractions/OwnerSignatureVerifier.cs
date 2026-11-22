namespace Bazaar.Approvals;

/// <summary>Why an owner signature was accepted or refused. Only <see cref="Valid"/> lets an action run.</summary>
public enum OwnerSignatureVerdict
{
    Valid,
    BadSignature,
    UnknownDevice,
    RevokedDevice,
    NotTheOwner,
    Expired,
    NonceReplayed,

    /// <summary>ExpiresAt is not after IssuedAt, or is further after it than the maximum lifetime.</summary>
    InvalidLifetime,

    /// <summary>IssuedAt is in the future beyond the allowed clock skew.</summary>
    IssuedInFuture,
}

/// <summary>
/// The one owner-signature check (doc 32 section 5a). Every owner-gated action goes through it: merges, module permission
/// approvals and legal holds. It fails closed, so anything that is not <see cref="OwnerSignatureVerdict.Valid"/> is refused.
/// </summary>
public interface IOwnerSignatureVerifier
{
    /// <summary>Checks the signature, the key, its owner, the expiry and the nonce. The nonce is spent only on a valid signature.</summary>
    Task<OwnerSignatureVerdict> VerifyAsync(OwnerPayload payload, byte[] signature, DateTimeOffset now, CancellationToken cancellationToken);
}
