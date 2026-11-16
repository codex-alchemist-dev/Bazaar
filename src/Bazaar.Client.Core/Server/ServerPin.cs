using System.Net.Http;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Bazaar.Client.Core.Server;

/// <summary>
/// The key-hash pin of the company server (doc 37 section 4a, doc 32 section 2). The pin is the lowercase hex SHA-256 of the
/// server certificate's SubjectPublicKeyInfo, the same value the server computes in <c>PublicKeyPin</c>. A pinned connection
/// accepts only a certificate whose key matches the pin, and it ignores chain errors, so a renewed certificate with the same
/// key keeps working and any other key fails closed.
/// </summary>
public static class ServerPin
{
    /// <summary>Lowercase hex SHA-256 of the certificate's SubjectPublicKeyInfo DER (64 characters).</summary>
    public static string Compute(X509Certificate2 certificate)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        return Convert.ToHexStringLower(SHA256.HashData(certificate.PublicKey.ExportSubjectPublicKeyInfo()));
    }

    /// <summary>True when <paramref name="pin"/> is exactly 64 lowercase hex characters.</summary>
    public static bool IsWellFormed(string? pin)
    {
        if (pin is null || pin.Length != 64) return false;
        foreach (var c in pin)
        {
            if (!(c is >= '0' and <= '9' or >= 'a' and <= 'f')) return false;
        }
        return true;
    }

    /// <summary>True when the certificate's key matches <paramref name="pin"/>.</summary>
    public static bool Matches(X509Certificate certificate, string pin)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        if (!IsWellFormed(pin)) return false;
        // Only a copy we made is ours to dispose; the caller keeps the certificate it passed in.
        if (certificate is X509Certificate2 own) return string.Equals(Compute(own), pin, StringComparison.Ordinal);
        using var copy = new X509Certificate2(certificate);
        return string.Equals(Compute(copy), pin, StringComparison.Ordinal);
    }

    /// <summary>
    /// A handler that accepts a server only when its key matches <paramref name="pin"/>. The pin replaces CA trust, so chain
    /// errors are ignored on purpose; the key match is the whole check.
    /// </summary>
    /// <exception cref="ArgumentException">The pin is not 64 lowercase hex characters.</exception>
    public static SocketsHttpHandler CreatePinnedHandler(string pin)
    {
        if (!IsWellFormed(pin)) throw new ArgumentException("The key-hash pin must be 64 lowercase hex characters.", nameof(pin));
        return new SocketsHttpHandler
        {
            SslOptions = new SslClientAuthenticationOptions
            {
                RemoteCertificateValidationCallback = (_, certificate, _, _) =>
                    certificate is not null && Matches(certificate, pin),
            },
        };
    }
}
