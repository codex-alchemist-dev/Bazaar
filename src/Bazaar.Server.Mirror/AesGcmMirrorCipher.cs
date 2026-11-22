using System.Security.Cryptography;
using System.Text;

namespace Bazaar.Server.Mirror;

/// <summary>AES-256-GCM sealing for Mirror values, with a fresh 12-byte nonce per seal. Holds one fixed key; see <see cref="PersonKeyMirrorCipher"/> for the host one.</summary>
public sealed class AesGcmMirrorCipher : IMirrorCipher
{
    private const int _nonceSize = 12;
    private const int _tagSize = 16;
    private readonly byte[] _key;
    private readonly Guid _keyId;

    public AesGcmMirrorCipher(Guid keyId, ReadOnlySpan<byte> key)
    {
        if (key.Length != 32)
        {
            throw new ArgumentException("The key must be 32 bytes.", nameof(key));
        }

        _keyId = keyId;
        _key = key.ToArray();
    }

    public ValueTask<SealedMirrorValue> SealAsync(string plaintext, byte[] associatedData, CancellationToken cancellationToken) =>
        ValueTask.FromResult(Seal(_keyId, _key, plaintext, associatedData));

    public ValueTask<string> OpenAsync(SealedMirrorValue value, byte[] associatedData, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.KeyId != _keyId)
        {
            throw new CryptographicException("The value was not sealed with this key.");
        }

        return ValueTask.FromResult(Open(_key, value, associatedData));
    }

    internal static SealedMirrorValue Seal(Guid keyId, byte[] key, string plaintext, byte[] associatedData)
    {
        ArgumentNullException.ThrowIfNull(plaintext);
        var nonce = RandomNumberGenerator.GetBytes(_nonceSize);
        var data = Encoding.UTF8.GetBytes(plaintext);
        var output = new byte[data.Length + _tagSize];
        using var aes = new AesGcm(key, _tagSize);
        aes.Encrypt(nonce, data, output.AsSpan(0, data.Length), output.AsSpan(data.Length, _tagSize), associatedData);
        return new SealedMirrorValue(keyId, nonce, output);
    }

    internal static string Open(byte[] key, SealedMirrorValue value, byte[] associatedData)
    {
        if (value.Nonce.Length != _nonceSize || value.Ciphertext.Length < _tagSize)
        {
            throw new CryptographicException("The sealed value is malformed.");
        }

        var length = value.Ciphertext.Length - _tagSize;
        var plain = new byte[length];
        using var aes = new AesGcm(key, _tagSize);
        aes.Decrypt(value.Nonce, value.Ciphertext.AsSpan(0, length), value.Ciphertext.AsSpan(length, _tagSize), plain, associatedData);
        return Encoding.UTF8.GetString(plain);
    }
}
