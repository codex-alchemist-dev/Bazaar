using System.Security.Cryptography;

namespace Bazaar.Server.Identity.Keys;

/// <summary>
/// Default key-encryption key: AES-256-GCM under a random 32-byte key kept in a file in the data root. The file is created
/// on first use with exclusive create and owner-only permissions on POSIX hosts. The key bytes are never logged or returned
/// outside this class. This is a floor, not custody: anyone who can read the data root can read the key, so a host should
/// register a platform provider (DPAPI, Keystore, a vault) through <see cref="IKekProvider"/> when one exists.
/// The default is a floor under the data-root ACL on Windows and the 0600 mode on POSIX; see docs/design/14.
/// </summary>
public sealed class FileKekProvider : IKekProvider, IDisposable
{
    /// <summary>Stable id stored with each wrap, so a later rotation can find old wraps.</summary>
    public const string Id = "file-kek-v1";

    private const int _keySize = 32;
    private const int _nonceSize = 12;
    private const int _tagSize = 16;

    private readonly byte[] _key;

    private FileKekProvider(byte[] key) => _key = key;

    /// <summary>The KEK id this provider writes into each wrap.</summary>
    public string KekId => Id;

    /// <summary>Opens the key file at <paramref name="keyPath"/>, creating it with a fresh random key when it does not exist.</summary>
    /// <exception cref="InvalidDataException">The file exists but is not a 32-byte key.</exception>
    public static FileKekProvider Open(string keyPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(keyPath);
        if (!Path.IsPathFullyQualified(keyPath))
            throw new ArgumentException("The KEK file path must be absolute.", nameof(keyPath));

        var directory = Path.GetDirectoryName(keyPath)
            ?? throw new ArgumentException("The KEK file path has no directory.", nameof(keyPath));
        Directory.CreateDirectory(directory);

        if (!File.Exists(keyPath))
        {
            var fresh = RandomNumberGenerator.GetBytes(_keySize);
            try
            {
                // CreateNew fails if another start created the file first; that start's key is then read below.
                using var stream = new FileStream(keyPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                stream.Write(fresh);
                stream.Flush(flushToDisk: true);
            }
            catch (IOException) when (File.Exists(keyPath))
            {
                // Lost the race to another start; fall through and read its key.
            }
            finally
            {
                Array.Clear(fresh);
            }

            RestrictToOwner(keyPath);
        }

        var key = File.ReadAllBytes(keyPath);
        if (key.Length != _keySize)
        {
            Array.Clear(key);
            throw new InvalidDataException("The KEK file does not hold a 32-byte key.");
        }

        return new FileKekProvider(key);
    }

    /// <inheritdoc />
    public ValueTask<byte[]> WrapAsync(ReadOnlyMemory<byte> dek, ReadOnlyMemory<byte> associatedData, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var nonce = RandomNumberGenerator.GetBytes(_nonceSize);
        var cipher = new byte[dek.Length];
        var tag = new byte[_tagSize];
        using (var aes = new AesGcm(_key, _tagSize))
        {
            aes.Encrypt(nonce, dek.Span, cipher, tag, associatedData.Span);
        }

        // Layout: nonce | ciphertext | tag.
        var wrapped = new byte[nonce.Length + cipher.Length + tag.Length];
        nonce.CopyTo(wrapped, 0);
        cipher.CopyTo(wrapped, nonce.Length);
        tag.CopyTo(wrapped, nonce.Length + cipher.Length);
        return ValueTask.FromResult(wrapped);
    }

    /// <inheritdoc />
    public ValueTask<byte[]> UnwrapAsync(ReadOnlyMemory<byte> wrappedDek, ReadOnlyMemory<byte> associatedData, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var wrapped = wrappedDek.Span;
        if (wrapped.Length < _nonceSize + _tagSize)
            throw new KekUnwrapException("The wrapped key is too short.");

        var nonce = wrapped[.._nonceSize];
        var cipher = wrapped[_nonceSize..^_tagSize];
        var tag = wrapped[^_tagSize..];
        var plain = new byte[cipher.Length];
        try
        {
            using var aes = new AesGcm(_key, _tagSize);
            aes.Decrypt(nonce, cipher, tag, plain, associatedData.Span);
        }
        catch (CryptographicException)
        {
            Array.Clear(plain);
            throw new KekUnwrapException("The wrapped key does not authenticate under this KEK and associated data.");
        }

        return ValueTask.FromResult(plain);
    }

    /// <summary>Clears the key bytes held in memory.</summary>
    public void Dispose() => Array.Clear(_key);

    private static void RestrictToOwner(string keyPath)
    {
        // Windows relies on the data root's ACL; the owner-only mode is POSIX only.
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(keyPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }
}
