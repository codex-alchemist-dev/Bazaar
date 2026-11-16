using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Bazaar.Updates;

/// <summary>
/// Verifies update packages against the company's signing certificate, which is pinned into the company build
/// (docs 37 and 32: the key pin is baked in, so a fresh client needs no pairing code to trust its server).
/// The signature is RSASSA-PKCS1-v1_5 over SHA-256 of the package bytes, made with the certificate's private key.
/// Only the public key is held here. Platform-neutral: no CNG or DPAPI is needed to verify.
/// </summary>
public sealed class PinnedCertificateSignatureVerifier : IUpdateSignatureVerifier
{
    private readonly RSA _publicKey;

    /// <summary>Pins one certificate. Only RSA keys are accepted, and the certificate must be a signing certificate.</summary>
    public PinnedCertificateSignatureVerifier(X509Certificate2 pinned)
    {
        ArgumentNullException.ThrowIfNull(pinned);

        var rsa = pinned.GetRSAPublicKey()
            ?? throw new ArgumentException("The pinned certificate does not hold an RSA public key.", nameof(pinned));

        // Keep a copy of only the public parameters, so the caller's certificate can be disposed freely.
        _publicKey = RSA.Create(rsa.ExportParameters(includePrivateParameters: false));
        Thumbprint = pinned.Thumbprint;
    }

    /// <summary>The thumbprint of the pinned certificate, for logs and for matching the company build.</summary>
    public string Thumbprint { get; }

    /// <inheritdoc />
    public bool Verify(ReadOnlySpan<byte> package, ReadOnlySpan<byte> signature)
    {
        if (signature.IsEmpty)
        {
            return false;
        }

        Span<byte> digest = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(package, digest);
        return _publicKey.VerifyHash(digest, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
    }
}
