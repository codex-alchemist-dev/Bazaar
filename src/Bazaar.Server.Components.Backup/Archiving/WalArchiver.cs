using System.Security.Cryptography;

namespace Bazaar.Server.Components.Backup.Archiving;

/// <summary>A WAL file already archived under the same name with different bytes. Never overwritten.</summary>
public sealed class ArchiveConflictException(string fileName) : IOException($"Archive already holds {fileName} with different content.");

/// <summary>
/// Copies one WAL segment or backup file into an archive directory for <c>archive_command</c> (doc 33, PostgreSQL Windows path).
/// Fails loudly on any doubt, so PostgreSQL keeps the file and retries. A byte-identical existing file is success
/// (PostgreSQL may retry after a crash). The copy is fsynced, verified by SHA-256, then moved into place without overwrite.
/// </summary>
public static class WalArchiver
{
    /// <summary>Archives <paramref name="sourcePath"/> as <paramref name="fileName"/> inside <paramref name="archiveDirectory"/>.</summary>
    public static async Task ArchiveAsync(string sourcePath, string archiveDirectory, string fileName, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        if (fileName.Contains(Path.DirectorySeparatorChar) || fileName.Contains(Path.AltDirectorySeparatorChar) || fileName == "..")
            throw new ArgumentException("A WAL archive name must be a plain file name.", nameof(fileName));
        if (!File.Exists(sourcePath))
            throw new FileNotFoundException("Archive source is missing.", sourcePath);
        Directory.CreateDirectory(archiveDirectory);

        var sourceDigest = await HashFileAsync(sourcePath, ct).ConfigureAwait(false);
        if (new FileInfo(sourcePath).Length == 0)
            throw new IOException("Refusing to archive an empty file.");

        var destination = Path.Combine(archiveDirectory, fileName);
        if (File.Exists(destination))
        {
            var existing = await HashFileAsync(destination, ct).ConfigureAwait(false);
            if (CryptographicOperations.FixedTimeEquals(existing, sourceDigest))
                return;
            throw new ArchiveConflictException(fileName);
        }

        var partial = destination + ".partial-" + Guid.CreateVersion7().ToString("N");
        try
        {
            await CopyWithFsyncAsync(sourcePath, partial, ct).ConfigureAwait(false);
            var copyDigest = await HashFileAsync(partial, ct).ConfigureAwait(false);
            if (!CryptographicOperations.FixedTimeEquals(copyDigest, sourceDigest))
                throw new IOException("Archive copy does not match its source.");
            File.Move(partial, destination, overwrite: false);
            FsyncDirectory(archiveDirectory);
        }
        catch
        {
            TryDelete(partial);
            throw;
        }
    }

    private static async Task CopyWithFsyncAsync(string source, string target, CancellationToken ct)
    {
        await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16, FileOptions.Asynchronous | FileOptions.WriteThrough);
        await input.CopyToAsync(output, ct).ConfigureAwait(false);
        await output.FlushAsync(ct).ConfigureAwait(false);
        output.Flush(flushToDisk: true);
    }

    private static async Task<byte[]> HashFileAsync(string path, CancellationToken ct)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.Asynchronous);
        return await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false);
    }

    /// <summary>Directory fsync makes the rename durable on POSIX. Windows does not need it.</summary>
    private static void FsyncDirectory(string directory)
    {
        if (OperatingSystem.IsWindows())
            return;
        PosixDirectorySync.Flush(directory);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (IOException)
        {
            // The original failure is the one to report; a leftover partial file is harmless and is ignored by PostgreSQL.
        }
    }
}
