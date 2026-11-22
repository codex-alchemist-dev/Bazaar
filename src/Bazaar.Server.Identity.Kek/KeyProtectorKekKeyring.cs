using System.Runtime.Versioning;
using System.Security.Cryptography;

namespace Bazaar.Server.Identity.Kek;

/// <summary>
/// A keyring over an <see cref="IKeyProtector"/> (doc 61 Q2): on Windows the <c>file</c> provider's key file is sealed with DPAPI
/// (<see cref="ForDpapi"/>). DPAPI has no named entries, so <see cref="KekKeyringProbe.HasEntry"/> is always false; the associated
/// data is bound by storing its SHA-256 inside the protected blob and checking it on open.
/// </summary>
public sealed class KeyProtectorKekKeyring : IKekKeyring
{
    private const int _hashBytes = 32;
    private readonly IKeyProtector _protector;

    public KeyProtectorKekKeyring(IKeyProtector protector, string kind)
    {
        ArgumentNullException.ThrowIfNull(protector);
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        _protector = protector;
        Kind = kind;
    }

    /// <summary>The kind written into a key file sealed by DPAPI.</summary>
    public const string DpapiKind = "dpapi";

    /// <summary>
    /// The DPAPI keyring for the <c>file</c> provider, with its own entropy so a blob of the <c>dpapi</c> provider is not interchangeable.
    /// Current-user scope: another local account or another machine cannot open the file; the service account itself, and anyone who can act as it, can.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static KeyProtectorKekKeyring ForDpapi() => new(new DpapiKeyProtector("Bazaar.KEK.file.v1"u8, DataProtectionScope.CurrentUser), DpapiKind);

    public string Kind { get; }

    public ValueTask<KekKeyringProbe> ProbeAsync(string entryName, CancellationToken cancellationToken) =>
        ValueTask.FromResult(new KekKeyringProbe(true, false, null));

    public ValueTask<bool> VerifyCommitmentAsync(string entryName, ReadOnlyMemory<byte> key, CancellationToken cancellationToken) =>
        ValueTask.FromResult(false);

    public ValueTask<byte[]> SealAsync(string entryName, ReadOnlyMemory<byte> key, ReadOnlyMemory<byte> associatedData, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var plain = new byte[_hashBytes + key.Length];
        try
        {
            SHA256.HashData(associatedData.Span, plain);
            key.Span.CopyTo(plain.AsSpan(_hashBytes));
            return ValueTask.FromResult(_protector.Protect(plain));
        }
        catch (CryptographicException ex)
        {
            throw new KekKeyringException($"{Kind} refused to protect the KEK.", ex);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
        }
    }

    public ValueTask<byte[]> OpenAsync(string entryName, ReadOnlyMemory<byte> sealedKey, ReadOnlyMemory<byte> associatedData, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        byte[] plain;
        try
        {
            plain = _protector.Unprotect(sealedKey.Span);
        }
        catch (CryptographicException ex)
        {
            throw new KekKeyringException($"The sealed KEK file does not open under this host's {Kind}.", ex);
        }

        try
        {
            Span<byte> expected = stackalloc byte[_hashBytes];
            SHA256.HashData(associatedData.Span, expected);
            if (plain.Length <= _hashBytes || !CryptographicOperations.FixedTimeEquals(plain.AsSpan(0, _hashBytes), expected))
            {
                throw new KekKeyringException("The sealed KEK file is bound to another key file or entry.");
            }

            return ValueTask.FromResult(plain[_hashBytes..]);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
        }
    }
}
