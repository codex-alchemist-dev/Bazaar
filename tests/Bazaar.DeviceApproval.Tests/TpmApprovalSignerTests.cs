using System.Runtime.Versioning;
using System.Security.Cryptography;
using Bazaar.DeviceApproval;
using Bazaar.DeviceApproval.Windows;

namespace Bazaar.DeviceApproval.Tests;

/// <summary>
/// Runs against the real TPM provider on this machine when it can create the key. On a machine without a usable TPM
/// the test asserts the named refusal instead, so both outcomes are covered and nothing is skipped.
/// </summary>
[SupportedOSPlatform("windows10.0.19041.0")]
public sealed class TpmApprovalSignerTests : IDisposable
{
    private readonly List<string> _keyNames = [];

    public void Dispose()
    {
        foreach (var name in _keyNames)
        {
            try
            {
                if (CngKey.Exists(name, CngProvider.MicrosoftPlatformCryptoProvider, CngKeyOpenOptions.None))
                {
                    using var key = CngKey.Open(name, CngProvider.MicrosoftPlatformCryptoProvider, CngKeyOpenOptions.None);
                    key.Delete();
                }
            }
            catch (CryptographicException)
            {
                // Best-effort cleanup; the key is test-only and unique per run.
            }
        }
    }

    private string NewKeyName()
    {
        var name = $"bazaar-test-approval-{Guid.NewGuid():N}";
        _keyNames.Add(name);
        return name;
    }

    [Fact]
    public async Task Signer_produces_a_tpm_signature_or_names_no_tpm()
    {
        try
        {
            using var signer = new TpmApprovalSigner(NewKeyName());
            // A TPM is present. The signature must verify under the TPM key's public half.
            var payload = "approve pr 685"u8.ToArray();
            var approval = await signer.SignAsync(payload, TestContext.Current.CancellationToken);

            Assert.Equal(ApprovalKeyCustody.Tpm, approval.Custody);
            Assert.Equal(64, approval.Signature.Length);
            Assert.True(VerifyWithTpmPublicKey(approval.KeyName, payload, approval.Signature));
        }
        catch (ApprovalRefusedException ex)
        {
            // No usable TPM on this machine: the refusal must be the named one.
            Assert.Equal(ApprovalRefusal.NoTpm, ex.Reason);
        }
    }

    [Fact]
    public async Task A_tampered_payload_fails_to_verify_against_the_tpm_key()
    {
        try
        {
            using var signer = new TpmApprovalSigner(NewKeyName());
            var payload = "approve pr 685"u8.ToArray();
            var approval = await signer.SignAsync(payload, TestContext.Current.CancellationToken);

            Assert.False(VerifyWithTpmPublicKey(approval.KeyName, "approve pr 999"u8.ToArray(), approval.Signature));
        }
        catch (ApprovalRefusedException ex)
        {
            Assert.Equal(ApprovalRefusal.NoTpm, ex.Reason);
        }
    }

    private static bool VerifyWithTpmPublicKey(string keyName, ReadOnlySpan<byte> payload, byte[] signature)
    {
        using var key = CngKey.Open(keyName, CngProvider.MicrosoftPlatformCryptoProvider, CngKeyOpenOptions.None);
        using var ecdsa = new ECDsaCng(key);
        return ecdsa.VerifyData(payload, signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
    }
}
