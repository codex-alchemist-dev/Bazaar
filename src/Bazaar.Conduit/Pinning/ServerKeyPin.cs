using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace Bazaar.Conduit.Pinning;

/// <summary>
/// Client-side check of the server key pin (doc 39 section 3, doc 32 section 2). The pin is the SHA-256 of the server
/// certificate's SubjectPublicKeyInfo, so it survives certificate renewal that keeps the key and ignores the address
/// the client dialled. A mismatch fails the attempt; there is no fallback to a weaker offer.
/// </summary>
public static class ServerKeyPin
{
    /// <summary>Prefix of every pin string.</summary>
    public const string Prefix = "sha256:";

    /// <summary>Warning returned for a third-party-terminated offer when the operator has allowed it without a pin.</summary>
    public const string UnpinnedWarning = "unpinned-tls-termination-allowed";

    /// <summary>Computes the pin of a certificate: <c>sha256:</c> and the lowercase hex digest of its SPKI.</summary>
    /// <param name="cert">Certificate whose public key is pinned.</param>
    public static string ComputeHash(X509Certificate2 cert)
    {
        ArgumentNullException.ThrowIfNull(cert);

        var spki = cert.PublicKey.ExportSubjectPublicKeyInfo();
        return Prefix + Convert.ToHexStringLower(SHA256.HashData(spki));
    }

    /// <summary>True when <paramref name="leaf"/> carries the key named by <paramref name="expectedHash"/>.</summary>
    /// <param name="expectedHash">The pin from the offer or the pairing payload.</param>
    /// <param name="leaf">The server's leaf certificate.</param>
    public static bool Matches(string expectedHash, X509Certificate2 leaf)
    {
        ArgumentNullException.ThrowIfNull(leaf);

        if (!IsWellFormed(expectedHash))
        {
            return false;
        }

        var actual = Encoding.UTF8.GetBytes(ComputeHash(leaf));
        var expected = Encoding.UTF8.GetBytes(expectedHash);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    /// <summary>
    /// Builds TLS client options whose certificate callback accepts only a leaf that matches the pin. Chain and name
    /// errors are ignored on purpose: for a pinned offer the pin is the trust, not the public CA chain.
    /// </summary>
    /// <param name="expectedHash">A <c>sha256:</c> pin. Empty or other forms are refused.</param>
    /// <param name="targetHost">Sent as SNI only; it is not checked against the certificate.</param>
    /// <exception cref="ArgumentException">The pin is not a <c>sha256:</c> value, or the host is blank.</exception>
    public static SslClientAuthenticationOptions CreatePinnedOptions(string expectedHash, string targetHost)
    {
        if (!IsWellFormed(expectedHash))
        {
            throw new ArgumentException("A sha256 server key pin is required.", nameof(expectedHash));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(targetHost);

        return new SslClientAuthenticationOptions
        {
            TargetHost = targetHost,
            RemoteCertificateValidationCallback = (_, certificate, _, _) => AcceptLeaf(expectedHash, certificate),
        };
    }

    /// <summary>
    /// Checks the pin rules of doc 39 section 3 for one offer. Returns null when the offer may be used, a reason when
    /// it must be refused, or <see cref="UnpinnedWarning"/> when the operator opt-out lets an unpinned third-party-TLS
    /// offer through. Callers treat only the exact warning as a warning; any other non-null value is a refusal.
    /// </summary>
    /// <remarks>
    /// A direct offer always needs a valid pin. A third-party-terminated offer (<see cref="ConduitOffer.TerminatesTls"/>)
    /// also needs a valid pin unless <paramref name="allowUnpinned"/> is set, because the client runs its own TLS
    /// session over the tunnel and checks the pin there.
    /// </remarks>
    /// <param name="offer">The offer to check.</param>
    /// <param name="allowUnpinned">The operator's <c>conduit.allow-unpinned-tls-termination</c> setting.</param>
    public static string? ValidatePinnedOffer(ConduitOffer offer, bool allowUnpinned)
    {
        ArgumentNullException.ThrowIfNull(offer);

        if (offer.TerminatesTls && allowUnpinned)
        {
            return UnpinnedWarning;
        }

        return IsWellFormed(offer.ServerKeyHash)
            ? null
            : "server key hash is missing or not sha256";
    }

    private static bool AcceptLeaf(string expectedHash, X509Certificate? certificate)
    {
        if (certificate is null)
        {
            return false;
        }

        if (certificate is X509Certificate2 leaf)
        {
            return Matches(expectedHash, leaf);
        }

        using var converted = new X509Certificate2(certificate);
        return Matches(expectedHash, converted);
    }

    private static bool IsWellFormed(string? hash) =>
        !string.IsNullOrEmpty(hash) && hash.StartsWith(Prefix, StringComparison.Ordinal) && hash.Length > Prefix.Length;
}
