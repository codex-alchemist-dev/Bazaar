using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Bazaar.Server.Components.Tls;

/// <summary>A loaded server certificate with its private key, and the SHA-256 hash of its public key for pinning.</summary>
/// <param name="Certificate">The certificate. It carries the private key, so the caller disposes this record when done.</param>
/// <param name="PublicKeyHash">Lowercase hex SHA-256 of the SubjectPublicKeyInfo. See <see cref="PublicKeyPin"/>.</param>
public sealed record ServerCertificate(X509Certificate2 Certificate, string PublicKeyHash) : IDisposable
{
    public void Dispose() => Certificate.Dispose();

    /// <summary>Builds the record from a certificate, refusing one without a private key (a listener cannot use it).</summary>
    internal static ServerCertificate Create(X509Certificate2 certificate, string origin)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        if (!certificate.HasPrivateKey)
            throw new TlsCertificateException(origin, "the certificate has no private key.");
        return new ServerCertificate(certificate, PublicKeyPin.Compute(certificate));
    }
}

/// <summary>
/// The public-key pin used for trust-on-first-use (docs/design/32 section 2). The hash covers the SubjectPublicKeyInfo, so
/// a renewed certificate with the same key keeps the same pin. Pairing stores and compares the value; this type only computes it.
/// </summary>
public static class PublicKeyPin
{
    /// <summary>Lowercase hex SHA-256 of the certificate's SubjectPublicKeyInfo DER (64 characters).</summary>
    public static string Compute(X509Certificate2 certificate)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        var spki = certificate.PublicKey.ExportSubjectPublicKeyInfo();
        return Convert.ToHexStringLower(SHA256.HashData(spki));
    }
}
