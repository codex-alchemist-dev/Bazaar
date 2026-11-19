using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;

namespace Bazaar.Server.Components.Remote;

/// <summary>
/// Refresh secrets (doc 32 section 5): 256 random bits, shown to the client once, stored only as a SHA-256 hash. Hashing the
/// presented text is enough because the secret is random and high-entropy; no salt or slow hash is needed.
/// </summary>
public static class RefreshCredentialSecret
{
    /// <summary>Random bytes in one secret (256 bits).</summary>
    public const int SecretBytes = 32;

    /// <summary>A new secret, base64url text of 256 random bits.</summary>
    public static string Generate() => Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(SecretBytes));

    /// <summary>The 32-byte SHA-256 hash of the secret text, as stored in <c>refresh_credential.hash</c>.</summary>
    public static byte[] Hash(string secret)
    {
        ArgumentException.ThrowIfNullOrEmpty(secret);
        return SHA256.HashData(Encoding.UTF8.GetBytes(secret));
    }
}
