using System;
using System.Security.Cryptography;

namespace Bazaar.Server.GitHub.Webhooks;

/// <summary>Verifies GitHub webhook signatures (X-Hub-Signature-256) with HMAC-SHA256 over the raw body.</summary>
public static class WebhookSignature
{
    private const string _prefix = "sha256=";
    private const int _hexLength = 64;

    /// <summary>
    /// Returns true only when the header is well formed and its digest equals HMAC-SHA256(secret, body).
    /// The comparison is constant time. Missing, malformed or wrong-length headers return false.
    /// </summary>
    /// <param name="secret">The webhook secret bytes. Must not be empty.</param>
    /// <param name="body">The raw request body, exactly as received.</param>
    /// <param name="signatureHeader">The X-Hub-Signature-256 header value, or null when absent.</param>
    public static bool Verify(ReadOnlySpan<byte> secret, ReadOnlySpan<byte> body, string? signatureHeader)
    {
        if (secret.IsEmpty)
        {
            throw new ArgumentException("The webhook secret must not be empty.", nameof(secret));
        }

        Span<byte> presented = stackalloc byte[HMACSHA256.HashSizeInBytes];
        if (!TryParseDigest(signatureHeader, presented))
        {
            return false;
        }

        Span<byte> expected = stackalloc byte[HMACSHA256.HashSizeInBytes];
        HMACSHA256.HashData(secret, body, expected);
        return CryptographicOperations.FixedTimeEquals(expected, presented);
    }

    /// <summary>Computes the header value GitHub would send for this secret and body.</summary>
    /// <param name="secret">The webhook secret bytes. Must not be empty.</param>
    /// <param name="body">The raw request body.</param>
    public static string Compute(ReadOnlySpan<byte> secret, ReadOnlySpan<byte> body)
    {
        if (secret.IsEmpty)
        {
            throw new ArgumentException("The webhook secret must not be empty.", nameof(secret));
        }

        byte[] digest = HMACSHA256.HashData(secret, body);
        return _prefix + Convert.ToHexStringLower(digest);
    }

    private static bool TryParseDigest(string? header, Span<byte> digest)
    {
        if (header is null || header.Length != _prefix.Length + _hexLength)
        {
            return false;
        }

        if (!header.StartsWith(_prefix, StringComparison.Ordinal))
        {
            return false;
        }

        try
        {
            byte[] decoded = Convert.FromHexString(header.AsSpan(_prefix.Length));
            if (decoded.Length != digest.Length)
            {
                return false;
            }

            decoded.CopyTo(digest);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
