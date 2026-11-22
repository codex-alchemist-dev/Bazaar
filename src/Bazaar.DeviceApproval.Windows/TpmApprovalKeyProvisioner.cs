using System.Runtime.Versioning;

namespace Bazaar.DeviceApproval.Windows;

/// <summary>
/// A TPM attestation source that has no attestation format yet. It returns no blobs, so the server's strict default refuses the key.
/// It is the default until the Windows TPM attestation (doc 32 section 5b step 3) is built; it never invents a format.
/// </summary>
public sealed class UnavailableTpmAttestationSource : IApprovalKeyAttestationSource
{
    /// <inheritdoc />
    public ValueTask<IReadOnlyList<byte[]>> AttestAsync(string keyName, ReadOnlyMemory<byte> nonce, CancellationToken cancellationToken) =>
        ValueTask.FromResult<IReadOnlyList<byte[]>>([]);
}

/// <summary>Creates the TPM approval key (docs/design/32, 5a.1) and exports what enrolment sends (5b).</summary>
[SupportedOSPlatform("windows10.0.19041.0")]
public sealed class TpmApprovalKeyProvisioner(IApprovalKeyAttestationSource? attestation = null) : IApprovalKeyProvisioner
{
    private readonly IApprovalKeyAttestationSource _attestation = attestation ?? new UnavailableTpmAttestationSource();

    /// <inheritdoc />
    public async ValueTask<ProvisionedApprovalKey> ProvisionAsync(string keyName, ReadOnlyMemory<byte> nonce, CancellationToken cancellationToken)
    {
        // The signer throws ApprovalRefusedException(NoTpm) when the TPM cannot hold the key: there is no software fallback.
        using var signer = new TpmApprovalSigner(keyName);
        var spki = signer.ExportPublicKeySpki();
        var blobs = await _attestation.AttestAsync(keyName, nonce, cancellationToken).ConfigureAwait(false);
        return new ProvisionedApprovalKey(spki, blobs, keyName);
    }
}
