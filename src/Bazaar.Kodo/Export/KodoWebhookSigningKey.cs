namespace Bazaar.Kodo.Export;

/// <summary>
/// A webhook signing key: an id that travels in the <c>Kodo-Key-Id</c> header and the secret that signs with it (doc 46 section 15.3).
/// The secret is never printed: <see cref="ToString"/> shows the id only, so a logged key cannot leak it.
/// </summary>
public sealed class KodoWebhookSigningKey
{
    /// <summary>The shortest secret accepted: 32 bytes, the HMAC-SHA256 block-friendly minimum for a random key.</summary>
    public const int MinimumSecretLength = 32;

    private readonly byte[] _secret;

    /// <summary>Creates a key. The secret is copied, so the caller may clear its own buffer.</summary>
    /// <param name="keyId">The key id. URL-safe characters, up to 64.</param>
    /// <param name="secret">The secret. At least <see cref="MinimumSecretLength"/> bytes, from a secure random source.</param>
    /// <exception cref="ArgumentException">The id or the secret is not acceptable.</exception>
    public KodoWebhookSigningKey(string keyId, ReadOnlySpan<byte> secret)
    {
        ArgumentNullException.ThrowIfNull(keyId);
        if (!KodoExportPatterns.KeyId().IsMatch(keyId))
        {
            throw new ArgumentException("A webhook key id must be 1 to 64 URL-safe characters.", nameof(keyId));
        }

        if (secret.Length < MinimumSecretLength)
        {
            throw new ArgumentException($"A webhook secret must be at least {MinimumSecretLength} bytes.", nameof(secret));
        }

        KeyId = keyId;
        _secret = secret.ToArray();
    }

    /// <summary>The key id, sent in the <c>Kodo-Key-Id</c> header.</summary>
    public string KeyId { get; }

    /// <summary>The secret, for the signer and the verifier in this assembly only.</summary>
    internal ReadOnlySpan<byte> Secret => _secret;

    /// <summary>Shows the key id and a redaction marker. The secret never appears.</summary>
    public override string ToString() => $"KodoWebhookSigningKey {{ KeyId = {KeyId}, Secret = [redacted] }}";
}
