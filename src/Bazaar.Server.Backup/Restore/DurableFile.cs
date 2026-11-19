using Bazaar.Server.Backup.Archiving;

namespace Bazaar.Server.Backup.Restore;

/// <summary>Crash-safe small-file replace: temp file, flush to disk, rename, then a POSIX directory flush.</summary>
internal static class DurableFile
{
    public static void WriteAtomically(string target, byte[] bytes, Action<string>? afterTempWritten = null)
    {
        var directory = Path.GetDirectoryName(target) ?? throw new IOException("The path has no directory.");
        Directory.CreateDirectory(directory);
        var temp = Path.Combine(directory, "." + Path.GetFileName(target) + "." + Guid.CreateVersion7().ToString("N") + ".tmp");
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }

            afterTempWritten?.Invoke(temp);
            File.Move(temp, target, overwrite: true);
            FsyncDirectory(directory);
        }
        catch
        {
            try
            {
                File.Delete(temp);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }

            throw;
        }
    }

    public static void FsyncDirectory(string directory)
    {
        if (!OperatingSystem.IsWindows())
        {
            PosixDirectorySync.Flush(directory);
        }
    }
}
