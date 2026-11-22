namespace Bazaar.Server.DataVolume.Linux;

/// <summary>
/// The file system access of the Linux data volume provider and operator, behind one seam so tests run without a device.
/// Reads return null (or an empty list) when the file is missing or unreadable; they never throw for that.
/// </summary>
internal interface ILinuxVolumeFiles
{
    /// <summary>The path with every symlink resolved, absolute and without <c>..</c>.</summary>
    string ResolvePath(string path);

    /// <summary>The text of a small kernel file (<c>/proc</c>, <c>/sys</c>), or null.</summary>
    string? ReadAllText(string path);

    /// <summary>The entry names of a directory, or an empty list.</summary>
    IReadOnlyList<string> ListDirectory(string path);

    /// <summary>At most <paramref name="count"/> bytes from the start of a file or block device, or null when it cannot be read.</summary>
    byte[]? ReadPrefix(string path, int count);

    bool FileExists(string path);

    bool DirectoryExists(string path);

    bool IsDirectoryEmpty(string path);

    long FileLength(string path);

    /// <summary>Creates a new empty file with the Unix mode, failing when it already exists.</summary>
    void CreateExclusiveFile(string path, UnixFileMode mode);

    void DeleteFile(string path);
}

/// <summary>The real file access.</summary>
internal sealed class SystemLinuxVolumeFiles : ILinuxVolumeFiles
{
    public string ResolvePath(string path)
    {
        var current = "/";
        foreach (var segment in Path.GetFullPath(path).Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            var target = new DirectoryInfo(current).ResolveLinkTarget(returnFinalTarget: true);
            if (target is not null)
                current = target.FullName;
        }

        return current;
    }

    public string? ReadAllText(string path)
    {
        try
        {
            return File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public IReadOnlyList<string> ListDirectory(string path)
    {
        try
        {
            return Directory.EnumerateFileSystemEntries(path).Select(Path.GetFileName).OfType<string>().ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    public byte[]? ReadPrefix(string path, int count)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var buffer = new byte[count];
            var read = stream.ReadAtLeast(buffer, count, throwOnEndOfStream: false);
            return buffer.AsSpan(0, read).ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public bool FileExists(string path) => File.Exists(path);

    public bool DirectoryExists(string path) => Directory.Exists(path);

    public bool IsDirectoryEmpty(string path) => !Directory.EnumerateFileSystemEntries(path).Any();

    public long FileLength(string path) => new FileInfo(path).Length;

    public void CreateExclusiveFile(string path, UnixFileMode mode)
    {
        if (!OperatingSystem.IsLinux())
            throw new PlatformNotSupportedException("The luks2 data volume provider runs on Linux only.");
        using var stream = new FileStream(path, new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None,
            UnixCreateMode = mode,
        });

        // The creation mode is reduced by the umask, so set it explicitly.
        File.SetUnixFileMode(path, mode);
    }

    public void DeleteFile(string path) => File.Delete(path);
}
