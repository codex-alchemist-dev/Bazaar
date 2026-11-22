namespace Bazaar.Server.Identity.Kek;

/// <summary>
/// The <c>file</c> KEK provider (doc 14 <c>identity.kek_provider</c>, used on hosts other than Windows): AES-256-GCM under a
/// random 32-byte key kept in a file in the data root. The file is created on first use with exclusive create and
/// owner-only permissions on POSIX hosts. This is a protected key file, not a hardware custody: anyone who can read the
/// data root can read the key.
/// </summary>
public sealed class FileKekProvider : AesGcmKekProvider
{
    /// <summary>Stable id stored with each wrap, so a later rotation can find old wraps.</summary>
    public const string Id = "file-kek-v1";

    private FileKekProvider(byte[] key)
        : base(Id, key)
    {
    }

    /// <summary>
    /// Opens the plain key file at <paramref name="keyPath"/>, creating it with a fresh random key when it does not exist. This is the
    /// protected-file path only; the host goes through <see cref="OpenAsync"/>, which seals the file with an OS keyring (doc 61 Q2).
    /// </summary>
    /// <exception cref="InvalidDataException">The file exists but is not a 32-byte key.</exception>
    public static FileKekProvider Open(string keyPath)
    {
        var key = KekKeyFile.OpenOrCreate(keyPath, seal: plain => (byte[])plain.Clone(), open: stored => (byte[])stored.Clone());
        return new FileKekProvider(key);
    }

    /// <summary>
    /// Opens the key file sealed by <paramref name="keyring"/> where it is usable, else the protected plain file with an audited warning
    /// (doc 61 Q2; see <see cref="KeyringKekKeyFile"/> for the rules). The KEK id stays <see cref="Id"/>, so existing wraps keep opening.
    /// </summary>
    /// <exception cref="KekKeyringException">The file is sealed and the keyring is unusable or refuses it, a downgrade was found, or <paramref name="mode"/> is required and no keyring is usable.</exception>
    /// <exception cref="InvalidDataException">The file is plain but not a 32-byte key.</exception>
    public static async ValueTask<FileKekProvider> OpenAsync(
        string keyPath,
        IKekKeyring? keyring,
        KekKeyringMode mode,
        IKekCustodyAuditSink audit,
        TimeProvider? time = null,
        CancellationToken cancellationToken = default)
    {
        var key = await KeyringKekKeyFile.OpenOrCreateAsync(keyPath, keyring, mode, audit, time ?? TimeProvider.System, cancellationToken).ConfigureAwait(false);
        return new FileKekProvider(key);
    }
}
