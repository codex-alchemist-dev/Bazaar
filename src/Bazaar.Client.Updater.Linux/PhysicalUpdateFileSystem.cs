using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Bazaar.Client.Updater.Linux;

/// <summary>The real disk, with Linux semantics for links and renames.</summary>
[UnsupportedOSPlatform("windows")]
public sealed partial class PhysicalUpdateFileSystem : IUpdateFileSystem
{
    /// <inheritdoc />
    public bool DirectoryExists(string path) => Directory.Exists(path);

    /// <inheritdoc />
    public bool FileExists(string path) => File.Exists(path);

    /// <inheritdoc />
    public void CreateDirectory(string path, UnixFileMode mode) => Directory.CreateDirectory(path, mode);

    /// <inheritdoc />
    public Stream OpenRead(string path) => new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);

    /// <inheritdoc />
    public Stream CreateFile(string path) =>
        new FileStream(path, new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None,
            UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
        });

    /// <inheritdoc />
    public void SetUnixMode(string path, UnixFileMode mode) => File.SetUnixFileMode(path, mode);

    /// <inheritdoc />
    public void DeleteFile(string path) => File.Delete(path);

    /// <inheritdoc />
    public void DeleteDirectory(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }

    /// <inheritdoc />
    public string? ReadLink(string path) => new FileInfo(path).LinkTarget;

    /// <inheritdoc />
    public void CreateSymbolicLink(string path, string target) => File.CreateSymbolicLink(path, target);

    /// <inheritdoc />
    public void RenameOver(string source, string destination)
    {
        // File.Move cannot be used here: it stats the source through the link, and a link to a directory is refused.
        if (Rename(source, destination) != 0)
        {
            throw new IOException($"Could not rename '{source}' over '{destination}': {Marshal.GetLastPInvokeErrorMessage()}");
        }
    }

    /// <inheritdoc />
    public void MoveDirectory(string source, string destination) => Directory.Move(source, destination);

    /// <inheritdoc />
    public IReadOnlyList<string> EnumerateDirectories(string path)
    {
        var result = new List<string>();
        foreach (string dir in Directory.EnumerateDirectories(path))
        {
            if (new DirectoryInfo(dir).LinkTarget is null)
            {
                result.Add(dir);
            }
        }

        return result;
    }

    [LibraryImport("libc", EntryPoint = "rename", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int Rename(string oldPath, string newPath);
}
