namespace Bazaar.Server.Identity.BrowserProfiles;

/// <summary>AEAD ciphertext with its nonce and tag.</summary>
public sealed record ProfileCiphertext(byte[] Nonce, byte[] Ciphertext, byte[] Tag);

/// <summary>
/// Encrypts browser profile payloads. The agent id is bound as associated data, so a blob cannot be moved to another
/// agent's profile and still decrypt. The production implementation wraps its key with the DEK from doc 14 WP3.
/// </summary>
public interface IProfileEncryptor
{
    /// <summary>Encrypts <paramref name="plaintext"/> for <paramref name="agentId"/> with a fresh nonce.</summary>
    ProfileCiphertext Encrypt(string agentId, byte[] plaintext);

    /// <summary>Decrypts and authenticates. Throws <see cref="System.Security.Cryptography.CryptographicException"/> on any tampering.</summary>
    byte[] Decrypt(string agentId, ProfileCiphertext ciphertext);
}
