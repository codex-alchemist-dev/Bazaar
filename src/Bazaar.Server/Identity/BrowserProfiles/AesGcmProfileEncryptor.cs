using System.Security.Cryptography;
using System.Text;

namespace Bazaar.Server.Identity.BrowserProfiles;

/// <summary>
/// AES-256-GCM encryptor keyed by a caller-supplied 32-byte key. This is an in-memory stand-in for the DEK: the real
/// key comes from the DEK wrap in doc 14 WP3 (not yet merged). Each call uses a fresh 96-bit nonce and a 128-bit tag.
/// </summary>
public sealed class AesGcmProfileEncryptor : IProfileEncryptor
{
    private const int _keySize = 32;
    private const int _nonceSize = 12;
    private const int _tagSize = 16;

    private readonly byte[] _key;

    /// <summary>Creates an encryptor. The key is copied.</summary>
    /// <exception cref="ArgumentException">The key is not 32 bytes.</exception>
    public AesGcmProfileEncryptor(byte[] key)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (key.Length != _keySize)
        {
            throw new ArgumentException($"AES-256-GCM needs a {_keySize}-byte key.", nameof(key));
        }

        _key = (byte[])key.Clone();
    }

    /// <inheritdoc />
    public ProfileCiphertext Encrypt(string agentId, byte[] plaintext)
    {
        ArgumentException.ThrowIfNullOrEmpty(agentId);
        ArgumentNullException.ThrowIfNull(plaintext);

        var nonce = RandomNumberGenerator.GetBytes(_nonceSize);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[_tagSize];
        using var aes = new AesGcm(_key, _tagSize);
        aes.Encrypt(nonce, plaintext, ciphertext, tag, Encoding.UTF8.GetBytes(agentId));
        return new ProfileCiphertext(nonce, ciphertext, tag);
    }

    /// <inheritdoc />
    public byte[] Decrypt(string agentId, ProfileCiphertext ciphertext)
    {
        ArgumentException.ThrowIfNullOrEmpty(agentId);
        ArgumentNullException.ThrowIfNull(ciphertext);

        var plaintext = new byte[ciphertext.Ciphertext.Length];
        using var aes = new AesGcm(_key, _tagSize);
        aes.Decrypt(ciphertext.Nonce, ciphertext.Ciphertext, ciphertext.Tag, plaintext, Encoding.UTF8.GetBytes(agentId));
        return plaintext;
    }
}
