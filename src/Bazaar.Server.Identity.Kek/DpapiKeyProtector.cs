using System.Runtime.Versioning;
using System.Security.Cryptography;

namespace Bazaar.Server.Identity.Kek;

/// <summary>
/// DPAPI protection of the KEK file (doc 14: <c>dpapi</c> on a Windows host, tied to the machine). Machine scope, so the
/// server service account can open the key, and a fixed entropy string binds the blob to this purpose.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class DpapiKeyProtector : IKeyProtector
{
    private readonly byte[] _entropy;

    /// <summary>Protects with the <c>dpapi</c> KEK provider's entropy.</summary>
    public DpapiKeyProtector()
        : this("Bazaar.KEK.v1"u8)
    {
    }

    /// <summary>Protects with <paramref name="entropy"/>, so blobs made for another purpose do not open here.</summary>
    public DpapiKeyProtector(ReadOnlySpan<byte> entropy) => _entropy = entropy.ToArray();

    /// <inheritdoc />
    public byte[] Protect(ReadOnlySpan<byte> plaintext) =>
        ProtectedData.Protect(plaintext.ToArray(), _entropy, DataProtectionScope.LocalMachine);

    /// <inheritdoc />
    public byte[] Unprotect(ReadOnlySpan<byte> protectedBytes) =>
        ProtectedData.Unprotect(protectedBytes.ToArray(), _entropy, DataProtectionScope.LocalMachine);
}
