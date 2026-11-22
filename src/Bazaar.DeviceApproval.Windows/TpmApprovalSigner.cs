using System.Runtime.Versioning;
using System.Security.Cryptography;

namespace Bazaar.DeviceApproval.Windows;

/// <summary>
/// Signs approvals with an ECDSA P-256 key held in the TPM (Microsoft Platform Crypto Provider), docs/design/32, 5a.1.
/// The key is non-exportable and has no software fallback: if the TPM cannot create or hold the key, the signer throws
/// <see cref="ApprovalRefusedException"/> with <see cref="ApprovalRefusal.NoTpm"/>, and nothing is signed.
/// </summary>
[SupportedOSPlatform("windows10.0.19041.0")]
public sealed class TpmApprovalSigner : IApprovalSigner, IDisposable
{
    private readonly ECDsa _key;
    private bool _disposed;

    /// <summary>Opens or creates the named approval key in the TPM. Throws <see cref="ApprovalRefusedException"/> when the TPM is unusable.</summary>
    public TpmApprovalSigner(string keyName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(keyName);
        KeyName = keyName;
        _key = OpenOrCreateTpmKey(keyName);
    }

    /// <summary>The name of the TPM key this signer uses.</summary>
    public string KeyName { get; }

    /// <summary>The public half as DER SubjectPublicKeyInfo, for enrolment (docs/design/32, 5b).</summary>
    public byte[] ExportPublicKeySpki()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _key.ExportSubjectPublicKeyInfo();
    }

    /// <inheritdoc />
    public ApprovalKeyCustody Custody => ApprovalKeyCustody.Tpm;

    /// <inheritdoc />
    public ValueTask<ApprovalSignature> SignAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(_disposed, this);

        // IEEE P1363 is the fixed 64-byte r || s form for P-256, the form the server checks.
        var signature = _key.SignData(payload.Span, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        return ValueTask.FromResult(new ApprovalSignature(signature, KeyName, ApprovalKeyCustody.Tpm));
    }

    /// <summary>Releases the handle to the TPM key. The key itself stays in the TPM.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _key.Dispose();
    }

    private static ECDsaCng OpenOrCreateTpmKey(string keyName)
    {
        var provider = CngProvider.MicrosoftPlatformCryptoProvider;
        try
        {
            if (!CngKey.Exists(keyName, provider, CngKeyOpenOptions.None))
            {
                var parameters = new CngKeyCreationParameters
                {
                    Provider = provider,
                    KeyUsage = CngKeyUsages.Signing,
                    KeyCreationOptions = CngKeyCreationOptions.None,
                    UIPolicy = new CngUIPolicy(CngUIProtectionLevels.None),
                };
                using var created = CngKey.Create(CngAlgorithm.ECDsaP256, keyName, parameters);
            }

            var key = CngKey.Open(keyName, provider, CngKeyOpenOptions.None);
            return new ECDsaCng(key);
        }
        catch (CryptographicException ex)
        {
            throw new ApprovalRefusedException(ApprovalRefusal.NoTpm, "This device has no usable TPM, so no approval key can be created or held.", ex);
        }
    }
}
