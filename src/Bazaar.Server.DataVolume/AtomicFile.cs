namespace Bazaar.Server.DataVolume;

/// <summary>Writes a small file through a temp file, a flush to disk and a rename, so a crash or a full disk never leaves a torn file.</summary>
internal static class AtomicFile
{
    public static async ValueTask WriteAsync(string path, byte[] content, bool overwrite, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(path) ?? throw new ArgumentException("The path has no directory.", nameof(path));
        Directory.CreateDirectory(directory);
        var temp = Path.Combine(directory, "." + Path.GetFileName(path) + "." + Guid.CreateVersion7().ToString("N") + ".tmp");
        try
        {
            await using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await stream.WriteAsync(content, cancellationToken);
                stream.Flush(flushToDisk: true);
            }

            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite);

            File.Move(temp, path, overwrite);
        }
        finally
        {
            File.Delete(temp);
        }
    }
}
