using System.Security.Cryptography;
using System.Text;

namespace Bazaar.Secrets;

/// <summary>AES-256-GCM sealing with a random 96-bit nonce per message and 128-bit tags.</summary>
internal static class SecretCrypto
{
    internal const int NonceBytes = 12;
    internal const int TagBytes = 16;

    /// <summary>Associated data that binds a wrapped data key to its scope and KEK.</summary>
    public static byte[] DataKeyAad(string scopeKey, string kekId) =>
        Encoding.UTF8.GetBytes($"bazaar.secrets.datakey.v1|{scopeKey}|{kekId}");

    /// <summary>Associated data that binds a secret version to its scope, name and version.</summary>
    public static byte[] SecretAad(string scopeKey, string name, int version) =>
        Encoding.UTF8.GetBytes($"bazaar.secrets.value.v1|{scopeKey}|{name}|{version}");

    public static SealedBlob Seal(ReadOnlySpan<byte> key, ReadOnlySpan<byte> plaintext, ReadOnlySpan<byte> associatedData)
    {
        var nonce = RandomNumberGenerator.GetBytes(NonceBytes);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[TagBytes];
        using var aes = new AesGcm(key, TagBytes);
        aes.Encrypt(nonce, plaintext, ciphertext, tag, associatedData);
        return new SealedBlob(nonce, ciphertext, tag);
    }

    /// <summary>Throws <see cref="CryptographicException"/> when the key, associated data or ciphertext does not match.</summary>
    public static byte[] Open(ReadOnlySpan<byte> key, ReadOnlySpan<byte> nonce, ReadOnlySpan<byte> ciphertext, ReadOnlySpan<byte> tag, ReadOnlySpan<byte> associatedData)
    {
        var plaintext = new byte[ciphertext.Length];
        using var aes = new AesGcm(key, TagBytes);
        aes.Decrypt(nonce, ciphertext, tag, plaintext, associatedData);
        return plaintext;
    }

    /// <summary>Packs nonce, ciphertext and tag into one array: the form a wrapped key is stored in.</summary>
    public static byte[] Pack(SealedBlob blob)
    {
        var packed = new byte[blob.Nonce.Length + blob.Ciphertext.Length + blob.Tag.Length];
        blob.Nonce.CopyTo(packed);
        blob.Ciphertext.CopyTo(packed.AsMemory(blob.Nonce.Length));
        blob.Tag.CopyTo(packed.AsMemory(blob.Nonce.Length + blob.Ciphertext.Length));
        return packed;
    }
}

internal sealed record SealedBlob(ReadOnlyMemory<byte> Nonce, ReadOnlyMemory<byte> Ciphertext, ReadOnlyMemory<byte> Tag);
