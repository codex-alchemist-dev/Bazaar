using System.Security.Cryptography;

namespace Bazaar.Server.Modules.Trust;

/// <summary>A trust store held in memory. Used by tests and by any caller that loads keys itself.</summary>
public sealed class InMemoryTrustedKeyStore : ITrustedKeyStore
{
    private readonly Dictionary<string, TrustedModuleKey> _keys = new(StringComparer.Ordinal);

    /// <summary>Adds a key. Rejects a duplicate key id and any key that is not on NIST P-256.</summary>
    /// <exception cref="ArgumentException">The key id is already present, or the key is not P-256.</exception>
    public void Add(TrustedModuleKey key)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (!IsNistP256(key.PublicKey))
        {
            throw new ArgumentException("Trusted module keys must be ECDSA P-256.", nameof(key));
        }

        if (!_keys.TryAdd(key.KeyId, key))
        {
            throw new ArgumentException("A trusted key with this id already exists.", nameof(key));
        }
    }

    /// <inheritdoc />
    public TrustedModuleKey? Find(string keyId)
    {
        ArgumentNullException.ThrowIfNull(keyId);
        return _keys.GetValueOrDefault(keyId);
    }

    /// <summary>True when the key is on the NIST P-256 curve.</summary>
    private static bool IsNistP256(ECDsa key)
    {
        var curve = key.ExportParameters(includePrivateParameters: false).Curve;
        return curve.Oid?.Value == ECCurve.NamedCurves.nistP256.Oid.Value;
    }
}
