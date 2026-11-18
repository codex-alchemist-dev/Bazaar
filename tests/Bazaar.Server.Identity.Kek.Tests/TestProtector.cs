using System.Security.Cryptography;

namespace Bazaar.Server.Identity.Kek.Tests;

/// <summary>
/// Stands in for DPAPI off Windows: AES-GCM under a key fixed per instance. A blob sealed by one instance does not open under another,
/// which is what a host change does to a real DPAPI blob.
/// </summary>
public sealed class TestProtector(byte[] key) : IKeyProtector
{
    public static TestProtector Create() => new(RandomNumberGenerator.GetBytes(32));

    public byte[] Protect(ReadOnlySpan<byte> plaintext)
    {
        var nonce = RandomNumberGenerator.GetBytes(12);
        var cipher = new byte[plaintext.Length];
        var tag = new byte[16];
        using var aes = new AesGcm(key, 16);
        aes.Encrypt(nonce, plaintext, cipher, tag);
        return [.. nonce, .. cipher, .. tag];
    }

    public byte[] Unprotect(ReadOnlySpan<byte> protectedBytes)
    {
        if (protectedBytes.Length < 28)
            throw new CryptographicException("Blob too short.");
        var nonce = protectedBytes[..12];
        var cipher = protectedBytes[12..^16];
        var tag = protectedBytes[^16..];
        var plain = new byte[cipher.Length];
        using var aes = new AesGcm(key, 16);
        aes.Decrypt(nonce, cipher, tag, plain);
        return plain;
    }
}
