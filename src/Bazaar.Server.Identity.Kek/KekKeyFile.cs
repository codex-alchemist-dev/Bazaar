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
        Directory.CreateDirectory(directory);

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

            Publish(keyPath, sealed_);
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

    /// <summary>
    /// Writes the sealed key to a private temp file, flushes it, and moves it into place without overwriting. A failed write (a full
    /// disk, a killed process) leaves no <c>kek.bin</c> behind, so the next start creates it again instead of choking on a torn file;
    /// a reader never sees a half-written key. If another start published first, its key wins.
    /// </summary>
    private static void Publish(string keyPath, byte[] sealedKey)
    {
        var temp = keyPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(sealedKey);
                stream.Flush(flushToDisk: true);
            }

            RestrictToOwner(temp);
            PlaceWithoutOverwrite(temp, keyPath);
        }
        catch (IOException) when (File.Exists(keyPath) && new FileInfo(keyPath).Length > 0)
        {
            // Lost the race to another start; its key is read below.
        }
        finally
        {
            try
            {
                File.Delete(temp);
            }
            catch (IOException)
            {
                // Best effort: a leftover temp file is harmless.
            }
        }
    }

    /// <summary>
    /// Puts the finished temp file at the key path only if nothing is there. Off Windows a rename replaces its target, and a
    /// check before it is not atomic, so two first starts could each publish a key and the second would replace the one the
    /// first had already read. A hard link fails when the name exists, in one step. Windows refuses a move onto an existing
    /// file by itself. A file system with no hard links falls back to the move.
    /// </summary>
    private static void PlaceWithoutOverwrite(string temp, string keyPath)
    {
        if (OperatingSystem.IsWindows())
        {
            File.Move(temp, keyPath, overwrite: false);
            return;
        }

        try
        {
            File.CreateHardLink(keyPath, temp);
        }
        catch (Exception error) when (error is UnauthorizedAccessException or NotSupportedException or PlatformNotSupportedException)
        {
            File.Move(temp, keyPath, overwrite: false);
        }
    }

    private static void RestrictToOwner(string keyPath)
    {
        // Windows relies on the data root's ACL; the owner-only mode is POSIX only.
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(keyPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }
}
