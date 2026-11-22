namespace Bazaar.Kodo.Runtime;

/// <summary>
/// A public key pinned in the install trust root (doc 46 section 6.1). <see cref="PublicKey"/> is the DER SubjectPublicKeyInfo,
/// and <see cref="Algorithm"/> names the verifier to use, so the envelope cannot choose its own algorithm.
/// </summary>
public sealed record TrustedKey(string KeyId, string Algorithm, byte[] PublicKey, DateTimeOffset NotBefore, DateTimeOffset NotAfter);

/// <summary>
/// The set of keys a runtime record may be signed with. Key ids are unique. A key is only usable inside its validity window
/// (the window is half-open: not before <see cref="TrustedKey.NotBefore"/>, and before <see cref="TrustedKey.NotAfter"/>).
/// </summary>
public sealed class RuntimeTrustRoot
{
    private readonly Dictionary<string, TrustedKey> _keys = new(StringComparer.Ordinal);

    /// <exception cref="ArgumentException">A key id is empty or repeated, or a validity window is empty.</exception>
    public RuntimeTrustRoot(IEnumerable<TrustedKey> keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        foreach (TrustedKey key in keys)
        {
            ArgumentNullException.ThrowIfNull(key);
            if (string.IsNullOrWhiteSpace(key.KeyId))
            {
                throw new ArgumentException("A trusted key needs a key id.", nameof(keys));
            }

            if (key.NotAfter <= key.NotBefore)
            {
                throw new ArgumentException($"Trusted key '{key.KeyId}' has an empty validity window.", nameof(keys));
            }

            if (!_keys.TryAdd(key.KeyId, key))
            {
                throw new ArgumentException($"Trusted key id '{key.KeyId}' is listed twice.", nameof(keys));
            }
        }
    }

    /// <summary>Number of pinned keys.</summary>
    public int Count => _keys.Count;

    /// <summary>The key with this id, or null when the id is not pinned.</summary>
    public TrustedKey? Find(string? keyId)
    {
        if (keyId is null)
        {
            return null;
        }

        return _keys.TryGetValue(keyId, out TrustedKey? key) ? key : null;
    }
}
