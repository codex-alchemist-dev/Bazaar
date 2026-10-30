using System.Security.Cryptography;
using System.Text;

namespace Bazaar.Server.Interrupts;

/// <summary>A sealed reason as stored in <c>interrupt_frame</c>. <see cref="Ciphertext"/> is the cipher output followed by the tag.</summary>
/// <param name="KeyId">The key that sealed the value.</param>
/// <param name="Nonce">The 12-byte cipher nonce, unique per seal.</param>
/// <param name="Ciphertext">The sealed bytes followed by the 16-byte authentication tag.</param>
public sealed record SealedReason(Guid KeyId, byte[] Nonce, byte[] Ciphertext);

/// <summary>Seals and opens interrupt reasons. The associated data binds each value to its row.</summary>
public interface IInterruptReasonCipher
{
    /// <summary>The key id written with every value this cipher seals.</summary>
    Guid KeyId { get; }

    /// <summary>Seals <paramref name="reason"/>. The same <paramref name="associatedData"/> must be given to <see cref="Open"/>.</summary>
    SealedReason Seal(string reason, ReadOnlySpan<byte> associatedData);

    /// <summary>Opens a sealed reason. Throws <see cref="CryptographicException"/> when the key, the data or the value is wrong.</summary>
    string Open(SealedReason value, ReadOnlySpan<byte> associatedData);
}

/// <summary>
/// AES-256-GCM over one key. The key comes from the caller: the personal-data key service (doc 01 WP6) supplies it in
/// production, and tests pass a fixed key.
/// </summary>
public sealed class AesGcmInterruptReasonCipher : IInterruptReasonCipher
{
    public const int KeySize = 32;
    private const int _nonceSize = 12;
    private const int _tagSize = 16;

    private readonly byte[] _key;

    public AesGcmInterruptReasonCipher(Guid keyId, ReadOnlySpan<byte> key)
    {
        if (key.Length != KeySize)
            throw new ArgumentException($"The interrupt reason key must be {KeySize} bytes.", nameof(key));
        KeyId = keyId;
        _key = key.ToArray();
    }

    /// <inheritdoc />
    public Guid KeyId { get; }

    /// <inheritdoc />
    public SealedReason Seal(string reason, ReadOnlySpan<byte> associatedData)
    {
        var plaintext = Encoding.UTF8.GetBytes(reason);
        var nonce = RandomNumberGenerator.GetBytes(_nonceSize);
        var ciphertext = new byte[plaintext.Length + _tagSize];
        using var aes = new AesGcm(_key, _tagSize);
        aes.Encrypt(nonce, plaintext, ciphertext.AsSpan(0, plaintext.Length), ciphertext.AsSpan(plaintext.Length), associatedData);
        return new SealedReason(KeyId, nonce, ciphertext);
    }

    /// <inheritdoc />
    public string Open(SealedReason value, ReadOnlySpan<byte> associatedData)
    {
        if (value.KeyId != KeyId)
            throw new CryptographicException("The interrupt reason was sealed under a different key.");
        if (value.Nonce.Length != _nonceSize || value.Ciphertext.Length < _tagSize)
            throw new CryptographicException("The sealed interrupt reason is malformed.");

        var length = value.Ciphertext.Length - _tagSize;
        var plaintext = new byte[length];
        using var aes = new AesGcm(_key, _tagSize);
        aes.Decrypt(value.Nonce, value.Ciphertext.AsSpan(0, length), value.Ciphertext.AsSpan(length), plaintext, associatedData);
        return Encoding.UTF8.GetString(plaintext);
    }
}
