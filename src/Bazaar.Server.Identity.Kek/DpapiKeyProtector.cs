using System.Runtime.Versioning;
using System.Security.Cryptography;

namespace Bazaar.Server.Identity.Kek;

/// <summary>
/// DPAPI protection of the KEK file (doc 14: <c>dpapi</c> on a Windows host). The default is machine scope, so any account on the host can
/// open it; <see cref="DataProtectionScope.CurrentUser"/> narrows that to the server's service account (the <c>file</c> keyring uses it,
/// doc 61). A fixed entropy string binds the blob to this purpose.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class DpapiKeyProtector : IKeyProtector
{
    private readonly byte[] _entropy;
    private readonly DataProtectionScope _scope;

    /// <summary>Protects with the <c>dpapi</c> KEK provider's entropy.</summary>
    public DpapiKeyProtector()
        : this("Bazaar.KEK.v1"u8)
    {
    }

    /// <summary>Protects with <paramref name="entropy"/>, so blobs made for another purpose do not open here.</summary>
    public DpapiKeyProtector(ReadOnlySpan<byte> entropy)
        : this(entropy, DataProtectionScope.LocalMachine)
    {
    }

    /// <summary>Protects with <paramref name="entropy"/> under <paramref name="scope"/>.</summary>
    public DpapiKeyProtector(ReadOnlySpan<byte> entropy, DataProtectionScope scope)
    {
        _entropy = entropy.ToArray();
        _scope = scope;
    }

    /// <inheritdoc />
    public byte[] Protect(ReadOnlySpan<byte> plaintext) =>
        ProtectedData.Protect(plaintext.ToArray(), _entropy, _scope);

    /// <inheritdoc />
    public byte[] Unprotect(ReadOnlySpan<byte> protectedBytes) =>
        ProtectedData.Unprotect(protectedBytes.ToArray(), _entropy, _scope);
}
