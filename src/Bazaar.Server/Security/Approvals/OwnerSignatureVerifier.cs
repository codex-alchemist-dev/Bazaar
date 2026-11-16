namespace Bazaar.Server.Security.Approvals;

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

/// <summary>Checks owner signatures against the owner key registry (doc 32 section 5a, doc 15 approvals).</summary>
public sealed class OwnerSignatureVerifier(IOwnerKeyRegistry keys, IOwnerNonceStore nonces) : IOwnerSignatureVerifier
{
    private readonly IOwnerKeyRegistry _keys = keys ?? throw new ArgumentNullException(nameof(keys));
    private readonly IOwnerNonceStore _nonces = nonces ?? throw new ArgumentNullException(nameof(nonces));

    /// <inheritdoc />
    public async Task<OwnerSignatureVerdict> VerifyAsync(
        OwnerPayload payload, byte[] signature, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentNullException.ThrowIfNull(signature);

        if (now >= payload.ExpiresAt)
        {
            return OwnerSignatureVerdict.Expired;
        }

        var key = await _keys.FindAsync(payload.DeviceId, cancellationToken).ConfigureAwait(false);
        if (key is null)
        {
            return OwnerSignatureVerdict.UnknownDevice;
        }

        if (key.Revoked)
        {
            return OwnerSignatureVerdict.RevokedDevice;
        }

        if (!string.Equals(key.OwnerId, payload.HumanId.ToString("D"), StringComparison.Ordinal))
        {
            return OwnerSignatureVerdict.NotTheOwner;
        }

        // The payload is rebuilt here from the fields, so a caller cannot sign one message and present another.
        var message = OwnerSignedPayload.Build(payload);
        if (!ApprovalSignature.Verify(key.PublicKeySpki, message, signature))
        {
            return OwnerSignatureVerdict.BadSignature;
        }

        // Spent last: a refused or forged request must not burn the owner's nonce.
        if (!await _nonces.TryUseAsync(payload.Nonce, payload.ExpiresAt, cancellationToken).ConfigureAwait(false))
        {
            return OwnerSignatureVerdict.NonceReplayed;
        }

        return OwnerSignatureVerdict.Valid;
    }
}
