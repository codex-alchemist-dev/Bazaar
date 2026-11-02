using System.Security.Cryptography;
using Bazaar.Contracts.Soul;

namespace Bazaar.Server.Souls.Bundles;

/// <summary>
/// Signs the exact bytes of <c>manifest.json</c> (docs/design/07-soul-system.md, "Export signing", Q6). The real signer
/// (exporting agent's GitHub App identity plus an operator key) is not built yet; see <see cref="FakeBundleSigner"/> for tests.
/// </summary>
public interface IBundleSigner
{
    /// <summary>Returns a detached signature over <paramref name="manifestBytes"/>.</summary>
    BundleSignature Sign(ReadOnlyMemory<byte> manifestBytes);
}

/// <summary>
/// Test and development signer. Either signs with an HMAC-SHA256 key supplied by the caller, or returns a fixed signature
/// regardless of input. It is not a production signer: HMAC proves nothing to a third party.
/// </summary>
public sealed class FakeBundleSigner : IBundleSigner
{
    /// <summary>Algorithm name written for the HMAC mode.</summary>
    public const string HmacAlgorithm = "HMAC-SHA256";

    private readonly BundleSignature? _fixed;
    private readonly string _keyId;
    private readonly byte[] _key;

    private FakeBundleSigner(string keyId, byte[] key, BundleSignature? fixedSignature)
    {
        _keyId = keyId;
        _key = key;
        _fixed = fixedSignature;
    }

    /// <summary>Signs with HMAC-SHA256 using <paramref name="key"/> (copied). The key must not be empty.</summary>
    public static FakeBundleSigner WithHmacKey(string keyId, ReadOnlySpan<byte> key)
    {
        ValidateKeyId(keyId);
        if (key.IsEmpty)
            throw new ArgumentException("The HMAC key must not be empty.", nameof(key));
        return new FakeBundleSigner(keyId, key.ToArray(), null);
    }

    /// <summary>Always returns <paramref name="signature"/>, whatever the manifest bytes are.</summary>
    public static FakeBundleSigner WithFixedSignature(BundleSignature signature)
    {
        ArgumentNullException.ThrowIfNull(signature);
        ValidateKeyId(signature.KeyId);
        return new FakeBundleSigner(signature.KeyId, [], signature);
    }

    /// <inheritdoc />
    public BundleSignature Sign(ReadOnlyMemory<byte> manifestBytes)
    {
        if (_fixed is not null)
            return _fixed;
        var mac = HMACSHA256.HashData(_key, manifestBytes.Span);
        return new BundleSignature(_keyId, HmacAlgorithm, Convert.ToBase64String(mac));
    }

    private static void ValidateKeyId(string keyId)
    {
        if (string.IsNullOrWhiteSpace(keyId))
            throw new ArgumentException("The key id must not be empty.", nameof(keyId));
    }
}
