using System.Security.Cryptography;
using Bazaar.Client.SignIn;

namespace Bazaar.Client.Windows;

/// <summary>
/// Seals the saved session with Windows DPAPI for the current user (doc 32 section 5), so only this person's Windows account can
/// read it back. A copy of the file on another account or machine opens as "not signed in".
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
internal sealed class DpapiSessionProtector : ISecretProtector
{
    // Binds the sealed bytes to this purpose, so another DPAPI blob cannot be swapped in.
    private static readonly byte[] _entropy = "bazaar.client.session.v1"u8.ToArray();

    /// <inheritdoc />
    public byte[] Protect(ReadOnlySpan<byte> plain) =>
        ProtectedData.Protect(plain.ToArray(), _entropy, DataProtectionScope.CurrentUser);

    /// <inheritdoc />
    public byte[]? Unprotect(ReadOnlySpan<byte> sealedBytes)
    {
        try
        {
            return ProtectedData.Unprotect(sealedBytes.ToArray(), _entropy, DataProtectionScope.CurrentUser);
        }
        catch (CryptographicException)
        {
            return null;
        }
    }
}
