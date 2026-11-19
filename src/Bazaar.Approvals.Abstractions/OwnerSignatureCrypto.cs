using System.Security.Cryptography;

namespace Bazaar.Approvals;

/// <summary>The ECDSA P-256 check every owner signature uses (doc 32 section 5a, doc 15).</summary>
public static class OwnerSignatureCrypto
{
    /// <summary>A P-256 signature in IEEE P1363 form: r and s, 32 bytes each.</summary>
    public const int SignatureLength = 64;

    /// <summary>Verifies a signature against a device's public key. Returns false, never throws, for any malformed key or signature.</summary>
    public static bool Verify(byte[] publicKeySpki, byte[] message, byte[] signature)
    {
        ArgumentNullException.ThrowIfNull(publicKeySpki);
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(signature);

        if (signature.Length != SignatureLength)
        {
            return false;
        }

        try
        {
            using var key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(publicKeySpki, out _);
            if (key.KeySize != 256)
            {
                return false;
            }

            return key.VerifyData(message, signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        }
        catch (CryptographicException)
        {
            return false;
        }
    }
}
