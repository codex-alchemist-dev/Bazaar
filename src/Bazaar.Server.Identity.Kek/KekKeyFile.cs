using System.Security.Cryptography;

namespace Bazaar.Server.Identity.Kek;

/// <summary>
/// Opens the KEK file at an absolute path, creating it with a fresh random key when it is absent. The bytes on disk go
/// through <c>seal</c> when created and <c>open</c> when read, so DPAPI and the plain file share one create-race and
/// permission path. Key bytes are never logged or returned outside the caller.
/// </summary>
internal static class KekKeyFile
{
    public const int KeySize = 32;

    public static byte[] OpenOrCreate(string keyPath, Func<byte[], byte[]> seal, Func<byte[], byte[]> open)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(keyPath);
        if (!Path.IsPathFullyQualified(keyPath))
            throw new ArgumentException("The KEK file path must be absolute.", nameof(keyPath));

        var directory = Path.GetDirectoryName(keyPath)
            ?? throw new ArgumentException("The KEK file path has no directory.", nameof(keyPath));
        CreateKeyDirectory(directory);

        if (!File.Exists(keyPath))
        {
            var fresh = RandomNumberGenerator.GetBytes(KeySize);
            byte[] sealed_;
            try
            {
                sealed_ = seal(fresh);
            }
            finally
            {
                Array.Clear(fresh);
            }

            try
            {
                // CreateNew fails if another start created the file first; that start's key is read below.
                // The owner-only mode is set at creation, so the key is never readable by others between create and chmod.
                var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
                if (!OperatingSystem.IsWindows())
                    options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
                using var stream = new FileStream(keyPath, options);
                stream.Write(sealed_);
                stream.Flush(flushToDisk: true);
            }
            catch (IOException) when (File.Exists(keyPath))
            {
                // Lost the race to another start; fall through and read its key.
            }

            RestrictToOwner(keyPath);
        }

        var bytes = File.ReadAllBytes(keyPath);
        byte[] key;
        try
        {
            key = open(bytes);
        }
        finally
        {
            Array.Clear(bytes);
        }

        if (key.Length != KeySize)
        {
            Array.Clear(key);
            throw new InvalidDataException("The KEK file does not hold a 32-byte key.");
        }

        return key;
    }

    /// <summary>Creates the key directory; on POSIX hosts a new one is owner-only. Windows relies on the data root's ACL (doc 61).</summary>
    internal static void CreateKeyDirectory(string directory)
    {
        if (OperatingSystem.IsWindows())
            Directory.CreateDirectory(directory);
        else
            Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    private static void RestrictToOwner(string keyPath)
    {
        // Windows relies on the data root's ACL; the owner-only mode is POSIX only.
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(keyPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }
}
