using System.Security.Cryptography;

namespace Bazaar.Identity.Kek;

/// <summary>
/// The <c>dpapi</c> KEK provider (doc 14 DECIDED 2026-11-16): the 32-byte KEK is stored in the key file protected by the
/// host's DPAPI (through <see cref="IKeyProtector"/>), and wraps use the same AES-256-GCM as the file provider. The
/// plaintext key exists only in memory. A key file from another host, or from the <c>file</c> provider, does not open.
/// </summary>
public sealed class DpapiKekProvider : AesGcmKekProvider
{
    /// <summary>Stable id stored with each wrap. A DPAPI wrap does not open under <see cref="FileKekProvider.Id"/> and the reverse.</summary>
    public const string Id = "dpapi-kek-v1";

    private DpapiKekProvider(byte[] key)
        : base(Id, key)
    {
    }

    /// <summary>Opens the protected key file at <paramref name="keyPath"/>, creating it when absent.</summary>
    /// <exception cref="InvalidDataException">The file is not a DPAPI blob for this host, or does not hold a 32-byte key.</exception>
    public static DpapiKekProvider Open(string keyPath, IKeyProtector protector)
    {
        ArgumentNullException.ThrowIfNull(protector);
        var key = KekKeyFile.OpenOrCreate(
            keyPath,
            seal: plain => protector.Protect(plain),
            open: stored =>
            {
                try
                {
                    return protector.Unprotect(stored);
                }
                catch (CryptographicException ex)
                {
                    throw new InvalidDataException(
                        "The KEK file is not protected by this host's DPAPI. Re-wrap is needed before switching identity.kek_provider (doc 14).",
                        ex);
                }
            });
        return new DpapiKekProvider(key);
    }
}
