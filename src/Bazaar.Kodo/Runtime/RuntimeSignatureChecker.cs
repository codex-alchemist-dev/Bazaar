namespace Bazaar.Kodo.Runtime;

/// <summary>What a signature check found. Only <see cref="Valid"/> lets a record through.</summary>
public enum RuntimeSignatureStatus
{
    /// <summary>The signature is valid under a trusted key that is inside its validity window.</summary>
    Valid,

    /// <summary>No signature is present.</summary>
    NoSignature,

    /// <summary>The key id is missing or not in the trust root.</summary>
    UnknownKey,

    /// <summary>The key is pinned but outside its validity window at the time checked.</summary>
    KeyOutsideValidity,

    /// <summary>The trusted key names an algorithm this build has no verifier for.</summary>
    UnsupportedAlgorithm,

    /// <summary>The signature is not valid for the message, or is not base64.</summary>
    BadSignature,
}

/// <summary>
/// Checks a detached signature over canonical bytes against the trust root. The caller passes the time, so the check is pure.
/// </summary>
public sealed class RuntimeSignatureChecker
{
    private readonly RuntimeTrustRoot _trust;
    private readonly Dictionary<string, IRuntimeSignatureVerifier> _verifiers = new(StringComparer.Ordinal);

    /// <summary>Creates a checker that verifies RSA PKCS#1 SHA-256 signatures.</summary>
    public RuntimeSignatureChecker(RuntimeTrustRoot trust)
        : this(trust, [new RsaPkcs1Sha256Verifier()])
    {
    }

    /// <exception cref="ArgumentException">Two verifiers claim the same algorithm name.</exception>
    public RuntimeSignatureChecker(RuntimeTrustRoot trust, IEnumerable<IRuntimeSignatureVerifier> verifiers)
    {
        _trust = trust ?? throw new ArgumentNullException(nameof(trust));
        ArgumentNullException.ThrowIfNull(verifiers);
        foreach (IRuntimeSignatureVerifier verifier in verifiers)
        {
            ArgumentNullException.ThrowIfNull(verifier);
            if (!_verifiers.TryAdd(verifier.Algorithm, verifier))
            {
                throw new ArgumentException($"Two verifiers claim the algorithm '{verifier.Algorithm}'.", nameof(verifiers));
            }
        }
    }

    /// <summary>Checks <paramref name="signature"/> (base64) over <paramref name="canonicalBody"/> for the key named by <paramref name="keyId"/>.</summary>
    public RuntimeSignatureStatus Check(ReadOnlySpan<byte> canonicalBody, string? keyId, string? signature, DateTimeOffset now)
    {
        if (string.IsNullOrEmpty(signature))
        {
            return RuntimeSignatureStatus.NoSignature;
        }

        TrustedKey? key = _trust.Find(keyId);
        if (key is null)
        {
            return RuntimeSignatureStatus.UnknownKey;
        }

        if (now < key.NotBefore || now >= key.NotAfter)
        {
            return RuntimeSignatureStatus.KeyOutsideValidity;
        }

        if (!_verifiers.TryGetValue(key.Algorithm, out IRuntimeSignatureVerifier? verifier))
        {
            return RuntimeSignatureStatus.UnsupportedAlgorithm;
        }

        byte[] signatureBytes = new byte[(signature.Length * 3) / 4 + 3];
        if (!Convert.TryFromBase64String(signature, signatureBytes, out int written))
        {
            return RuntimeSignatureStatus.BadSignature;
        }

        return verifier.Verify(canonicalBody, signatureBytes.AsSpan(0, written), key.PublicKey)
            ? RuntimeSignatureStatus.Valid
            : RuntimeSignatureStatus.BadSignature;
    }
}
