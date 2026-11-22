using System.Security.Cryptography;
using System.Text;

namespace Bazaar.Server.Mirror;

/// <summary>AES-256-GCM sealing for Mirror values, with a fresh 12-byte nonce per seal.</summary>
public sealed class AesGcmMirrorCipher : IMirrorCipher
{
    private const int _nonceSize = 12;
    private const int _tagSize = 16;
    private readonly byte[] _key;

    public AesGcmMirrorCipher(Guid keyId, ReadOnlySpan<byte> key)
    {
        if (key.Length != 32)
        {
            throw new ArgumentException("The key must be 32 bytes.", nameof(key));
        }

        KeyId = keyId;
        _key = key.ToArray();
    }

    public Guid KeyId { get; }

    public SealedMirrorValue Seal(string plaintext, ReadOnlySpan<byte> associatedData)
    {
        ArgumentNullException.ThrowIfNull(plaintext);
        var nonce = RandomNumberGenerator.GetBytes(_nonceSize);
        var data = Encoding.UTF8.GetBytes(plaintext);
        var output = new byte[data.Length + _tagSize];
        using var aes = new AesGcm(_key, _tagSize);
        aes.Encrypt(nonce, data, output.AsSpan(0, data.Length), output.AsSpan(data.Length, _tagSize), associatedData);
        return new SealedMirrorValue(KeyId, nonce, output);
    }

    public string Open(SealedMirrorValue value, ReadOnlySpan<byte> associatedData)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.KeyId != KeyId || value.Nonce.Length != _nonceSize || value.Ciphertext.Length < _tagSize)
        {
            throw new CryptographicException("The value was not sealed with this key.");
        }

        var length = value.Ciphertext.Length - _tagSize;
        var plain = new byte[length];
        using var aes = new AesGcm(_key, _tagSize);
        aes.Decrypt(value.Nonce, value.Ciphertext.AsSpan(0, length), value.Ciphertext.AsSpan(length, _tagSize), plain, associatedData);
        return Encoding.UTF8.GetString(plain);
    }
}
