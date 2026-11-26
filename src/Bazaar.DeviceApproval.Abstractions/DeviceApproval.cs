namespace Bazaar.DeviceApproval;

/// <summary>Where the approval key's private half lives. Only <see cref="Tpm"/> may approve a merge (docs/design/32, 5a).</summary>
public enum ApprovalKeyCustody
{
    /// <summary>The key is in the TPM and never leaves it.</summary>
    Tpm,

    /// <summary>The key is in software. It cannot approve a merge.</summary>
    Software,
}

/// <summary>Why an approval was refused. Every refusal is named; none falls back to a weaker path.</summary>
public enum ApprovalRefusal
{
    /// <summary>No usable TPM, so no approval key can be created.</summary>
    NoTpm,

    /// <summary>The key is not TPM-backed, so it cannot approve a merge.</summary>
    SoftwareKey,

    /// <summary>The person cancelled the presence check.</summary>
    PresenceCancelled,

    /// <summary>Presence is not available on this device (no Hello, disabled, or busy).</summary>
    PresenceUnavailable,

    /// <summary>The presence check failed.</summary>
    PresenceFailed,
}

/// <summary>The outcome of one presence check.</summary>
public enum PresenceOutcome
{
    Verified,
    Cancelled,
    Unavailable,
    Failed,
}

/// <summary>An approval signature and the key that made it. The server checks it against the enrolled public key.</summary>
/// <param name="Signature">ECDSA P-256 signature in IEEE P1363 form (r followed by s, 64 bytes).</param>
/// <param name="KeyName">The name of the TPM key that signed.</param>
/// <param name="Custody">The custody of that key. Always <see cref="ApprovalKeyCustody.Tpm"/> for a returned signature.</param>
public sealed record ApprovalSignature(byte[] Signature, string KeyName, ApprovalKeyCustody Custody);

/// <summary>Signs approval payloads with a device's approval key. Implemented per platform.</summary>
public interface IApprovalSigner
{
    /// <summary>The custody of the key this signer uses.</summary>
    ApprovalKeyCustody Custody { get; }

    /// <summary>Signs <paramref name="payload"/> (SHA-256 over the bytes is applied by the signer).</summary>
    ValueTask<ApprovalSignature> SignAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken);
}

/// <summary>Asks the person to prove presence for one approval (on Windows, Windows Hello). Implemented per platform.</summary>
public interface IPresenceVerifier
{
    /// <summary>Asks for a fresh presence check. <paramref name="reason"/> is shown to the person.</summary>
    ValueTask<PresenceOutcome> VerifyAsync(string reason, CancellationToken cancellationToken);
}

/// <summary>Thrown when an approval is refused. <see cref="Reason"/> names why.</summary>
public sealed class ApprovalRefusedException(ApprovalRefusal reason, string message, Exception? inner = null) : Exception(message, inner)
{
    public ApprovalRefusal Reason { get; } = reason;
}

/// <summary>
/// Gates one approval: the key must be TPM-backed, the person must pass a fresh presence check, and only then is the
/// payload signed. Each refusal throws <see cref="ApprovalRefusedException"/> and signs nothing. The gate is
/// platform-neutral; the platform supplies the signer and the presence check.
/// </summary>
public sealed class ApprovalGate(IApprovalSigner signer, IPresenceVerifier presence)
{
    private readonly IApprovalSigner _signer = signer ?? throw new ArgumentNullException(nameof(signer));
    private readonly IPresenceVerifier _presence = presence ?? throw new ArgumentNullException(nameof(presence));

    /// <summary>Approves <paramref name="payload"/>, asking for presence first. Throws when any step refuses.</summary>
    public async ValueTask<ApprovalSignature> ApproveAsync(ReadOnlyMemory<byte> payload, string reason, CancellationToken cancellationToken)
    {
        // A software-only key is refused before presence is asked, so the person is not prompted for nothing.
        if (_signer.Custody != ApprovalKeyCustody.Tpm)
        {
            throw new ApprovalRefusedException(ApprovalRefusal.SoftwareKey, "The approval key is not TPM-backed, so it cannot approve a merge.");
        }

        var outcome = await _presence.VerifyAsync(reason, cancellationToken).ConfigureAwait(false);
        switch (outcome)
        {
            case PresenceOutcome.Verified:
                break;
            case PresenceOutcome.Cancelled:
                throw new ApprovalRefusedException(ApprovalRefusal.PresenceCancelled, "The presence check was cancelled. Nothing was signed.");
            case PresenceOutcome.Unavailable:
                throw new ApprovalRefusedException(ApprovalRefusal.PresenceUnavailable, "Presence is not available on this device. Nothing was signed.");
            default:
                throw new ApprovalRefusedException(ApprovalRefusal.PresenceFailed, "The presence check failed. Nothing was signed.");
        }

        return await _signer.SignAsync(payload, cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>A freshly created approval key as the enrolment request needs it (docs/design/32, 5b step 2).</summary>
/// <param name="PublicKeySpki">The P-256 public key, DER SubjectPublicKeyInfo. The private half never leaves the hardware.</param>
/// <param name="Attestation">The platform's attestation of the key, one blob per chain element. Empty when the platform produced none; the server then refuses the key.</param>
/// <param name="KeyName">The name of the key on this device.</param>
public sealed record ProvisionedApprovalKey(byte[] PublicKeySpki, IReadOnlyList<byte[]> Attestation, string KeyName);

/// <summary>Creates (or opens) the device's hardware approval key for enrolment. Implemented per platform. Throws <see cref="ApprovalRefusedException"/> when no hardware key can be made.</summary>
public interface IApprovalKeyProvisioner
{
    /// <summary>Creates or opens the named key and asks the platform to attest it, binding <paramref name="nonce"/> where the platform allows.</summary>
    ValueTask<ProvisionedApprovalKey> ProvisionAsync(string keyName, ReadOnlyMemory<byte> nonce, CancellationToken cancellationToken);
}

/// <summary>
/// Produces the platform attestation of a hardware key (on Windows, the TPM attestation of doc 32 section 5b step 3). The wire
/// format is not fixed by the design yet, so it stays behind this interface; a source that cannot attest returns no blobs.
/// </summary>
public interface IApprovalKeyAttestationSource
{
    /// <summary>The attestation chain for <paramref name="keyName"/>, or an empty list when none can be produced.</summary>
    ValueTask<IReadOnlyList<byte[]>> AttestAsync(string keyName, ReadOnlyMemory<byte> nonce, CancellationToken cancellationToken);
}
