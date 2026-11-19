using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Bazaar.Kodo.Export;

/// <summary>The headers a signed webhook carries (doc 46 section 15.3).</summary>
/// <param name="KeyId">The value of <c>Kodo-Key-Id</c>.</param>
/// <param name="Timestamp">The value of <c>Kodo-Timestamp</c>: Unix seconds.</param>
/// <param name="Signature">The value of <c>Kodo-Signature</c>: <c>v1=</c> and the lowercase hex HMAC.</param>
public sealed record KodoWebhookSignature(string KeyId, long Timestamp, string Signature);

/// <summary>The result of checking a webhook's signature headers.</summary>
public enum KodoWebhookVerification
{
    /// <summary>The signature matches and the timestamp is inside the replay window.</summary>
    Valid,

    /// <summary>A header is missing or is not in the expected shape.</summary>
    Malformed,

    /// <summary>No key with the header's key id is supplied. A rotated-out key falls here.</summary>
    UnknownKey,

    /// <summary>The timestamp is more than the replay window away from now, in either direction.</summary>
    OutsideReplayWindow,

    /// <summary>The header is well formed and inside the window, but it does not match the body under that key.</summary>
    BadSignature,
}

/// <summary>
/// Signs and verifies Kodo webhooks (doc 46 section 15.3). The signed string is the decimal Unix timestamp, a dot, and the body bytes,
/// under HMAC-SHA256 (PROPOSED, doc 46 section 15.7). Verification takes every key that is valid at the moment, so two keys work
/// during a rotation.
/// </summary>
public static class KodoWebhookSigner
{
    /// <summary>The header that names the key.</summary>
    public const string KeyIdHeader = "Kodo-Key-Id";

    /// <summary>The header that carries the timestamp.</summary>
    public const string TimestampHeader = "Kodo-Timestamp";

    /// <summary>The header that carries the signature.</summary>
    public const string SignatureHeader = "Kodo-Signature";

    /// <summary>The scheme prefix of the signature value, so a new scheme can be added later.</summary>
    public const string SignaturePrefix = "v1=";

    /// <summary>How far the timestamp may sit from now, either way (doc 46 section 15.3: a 5 minute replay window).</summary>
    public static TimeSpan ReplayWindow { get; } = TimeSpan.FromMinutes(5);

    // The largest timestamp DateTimeOffset.FromUnixTimeSeconds accepts is 253402300799 (9999-12-31T23:59:59Z).
    private const long _maxUnixSeconds = 253402300799L;

    // HMAC-SHA256 output is 32 bytes.
    private const int _hmacSha256Size = 32;

    /// <summary>Signs a body at a given time.</summary>
    /// <param name="key">The signing key.</param>
    /// <param name="timestamp">The time to sign at. Only whole seconds are kept.</param>
    /// <param name="body">The exact bytes that will be sent.</param>
    public static KodoWebhookSignature Sign(KodoWebhookSigningKey key, DateTimeOffset timestamp, ReadOnlySpan<byte> body)
    {
        ArgumentNullException.ThrowIfNull(key);

        long unixSeconds = timestamp.ToUnixTimeSeconds();
        byte[] mac = ComputeMac(key, unixSeconds, body);
        return new KodoWebhookSignature(key.KeyId, unixSeconds, SignaturePrefix + Convert.ToHexStringLower(mac));
    }

    /// <summary>
    /// Checks the signature headers of a received webhook against the keys that are valid now.
    /// </summary>
    /// <param name="keys">Every key currently valid. During rotation this holds both the old and the new key.</param>
    /// <param name="keyId">The <c>Kodo-Key-Id</c> header.</param>
    /// <param name="timestampHeader">The <c>Kodo-Timestamp</c> header.</param>
    /// <param name="signatureHeader">The <c>Kodo-Signature</c> header.</param>
    /// <param name="body">The exact bytes received.</param>
    /// <param name="now">The receiver's current time.</param>
    public static KodoWebhookVerification Verify(
        IEnumerable<KodoWebhookSigningKey> keys,
        string? keyId,
        string? timestampHeader,
        string? signatureHeader,
        ReadOnlySpan<byte> body,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(keys);

        if (string.IsNullOrEmpty(keyId) || !TryParseHeaders(timestampHeader, signatureHeader, out long unixSeconds, out byte[] provided))
        {
            return KodoWebhookVerification.Malformed;
        }

        KodoWebhookSigningKey? key = keys.FirstOrDefault(candidate => string.Equals(candidate.KeyId, keyId, StringComparison.Ordinal));
        if (key is null)
        {
            return KodoWebhookVerification.UnknownKey;
        }

        TimeSpan skew = now - DateTimeOffset.FromUnixTimeSeconds(unixSeconds);
        if (skew.Duration() > ReplayWindow)
        {
            return KodoWebhookVerification.OutsideReplayWindow;
        }

        byte[] expected = ComputeMac(key, unixSeconds, body);
        return CryptographicOperations.FixedTimeEquals(expected, provided)
            ? KodoWebhookVerification.Valid
            : KodoWebhookVerification.BadSignature;
    }

    private static bool TryParseHeaders(string? timestampHeader, string? signatureHeader, out long unixSeconds, out byte[] provided)
    {
        unixSeconds = 0;
        provided = [];

        if (!long.TryParse(timestampHeader, NumberStyles.None, CultureInfo.InvariantCulture, out unixSeconds)
            || unixSeconds > _maxUnixSeconds
            || signatureHeader is null
            || !signatureHeader.StartsWith(SignaturePrefix, StringComparison.Ordinal))
        {
            return false;
        }

        try
        {
            provided = Convert.FromHexString(signatureHeader[SignaturePrefix.Length..]);
        }
        catch (FormatException)
        {
            return false;
        }

        return provided.Length == _hmacSha256Size;
    }

    private static byte[] ComputeMac(KodoWebhookSigningKey key, long unixSeconds, ReadOnlySpan<byte> body)
    {
        byte[] prefix = Encoding.ASCII.GetBytes(unixSeconds.ToString(CultureInfo.InvariantCulture) + ".");
        using IncrementalHash hmac = IncrementalHash.CreateHMAC(HashAlgorithmName.SHA256, key.Secret);
        hmac.AppendData(prefix);
        hmac.AppendData(body);
        return hmac.GetHashAndReset();
    }
}
