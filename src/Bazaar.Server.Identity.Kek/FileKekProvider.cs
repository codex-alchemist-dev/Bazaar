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

    /// <summary>Opens the key file at <paramref name="keyPath"/>, creating it with a fresh random key when it does not exist and <paramref name="mode"/> allows it.</summary>
    /// <exception cref="KekMissingException">The file is missing or empty and <paramref name="mode"/> is <see cref="KekOpenMode.OpenExisting"/>.</exception>
    /// <exception cref="InvalidDataException">The file exists but is not a 32-byte key.</exception>
    public static FileKekProvider Open(string keyPath, KekOpenMode mode = KekOpenMode.CreateIfMissing)
    {
        var key = KekKeyFile.OpenOrCreate(keyPath, seal: plain => (byte[])plain.Clone(), open: stored => (byte[])stored.Clone(), mode);
        return new FileKekProvider(key);
    }
}
