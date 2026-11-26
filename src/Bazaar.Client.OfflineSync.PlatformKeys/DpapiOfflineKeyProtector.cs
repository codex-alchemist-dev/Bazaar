using System.Runtime.Versioning;
using System.Security.Cryptography;

namespace Bazaar.Client.OfflineSync.PlatformKeys;

/// <summary>DPAPI protection in the current user's scope, with fixed entropy that binds the blob to the offline queue (doc 56 section 6).</summary>
[SupportedOSPlatform("windows")]
public sealed class DpapiOfflineKeyProtector : IOfflineKeyProtector
{
    private static readonly byte[] _entropy = "Bazaar.OfflineQueue.v1"u8.ToArray();

    /// <inheritdoc />
    public byte[] Protect(ReadOnlySpan<byte> plaintext) =>
        ProtectedData.Protect(plaintext.ToArray(), _entropy, DataProtectionScope.CurrentUser);

    /// <inheritdoc />
    public byte[] Unprotect(ReadOnlySpan<byte> protectedBytes) =>
        ProtectedData.Unprotect(protectedBytes.ToArray(), _entropy, DataProtectionScope.CurrentUser);
}
