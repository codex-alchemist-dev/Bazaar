using System.Security.Cryptography;

namespace Bazaar.Kodo.Runtime;

/// <summary>
/// One signature algorithm for runtime records, behind an interface so the algorithm can change without touching the verifiers.
/// Implementations never throw for bad input; a malformed key or signature is a false result.
/// </summary>
public interface IRuntimeSignatureVerifier
{
    /// <summary>The algorithm name that a <see cref="TrustedKey"/> uses to select this verifier.</summary>
    string Algorithm { get; }

    /// <summary>True only when <paramref name="signature"/> is a valid signature of <paramref name="message"/> under <paramref name="publicKey"/>.</summary>
    bool Verify(ReadOnlySpan<byte> message, ReadOnlySpan<byte> signature, ReadOnlySpan<byte> publicKey);
}

/// <summary>
/// RSASSA-PKCS1-v1_5 with SHA-256, the algorithm doc 37 names for signed release manifests. Keys must be at least 2048 bits.
/// PROPOSED: Ed25519 was the default if doc 37 were silent, but it does not name one.
/// </summary>
public sealed class RsaPkcs1Sha256Verifier : IRuntimeSignatureVerifier
{
    /// <summary>The name a trust root uses for this algorithm.</summary>
    public const string AlgorithmName = "rsa-pkcs1-sha256";

    /// <summary>The smallest RSA key this verifier accepts.</summary>
    public const int MinimumKeySizeBits = 2048;

    /// <inheritdoc />
    public string Algorithm => AlgorithmName;

    /// <inheritdoc />
    public bool Verify(ReadOnlySpan<byte> message, ReadOnlySpan<byte> signature, ReadOnlySpan<byte> publicKey)
    {
        try
        {
            using RSA rsa = RSA.Create();
            rsa.ImportSubjectPublicKeyInfo(publicKey, out _);
            if (rsa.KeySize < MinimumKeySizeBits)
            {
                return false;
            }

            return rsa.VerifyData(message, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        }
        catch (CryptographicException)
        {
            return false;
        }
    }
}
