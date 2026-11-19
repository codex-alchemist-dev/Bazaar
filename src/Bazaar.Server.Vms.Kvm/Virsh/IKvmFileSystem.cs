namespace Bazaar.Server.Vms.Kvm.Virsh;

/// <summary>The few file operations the virsh adapter needs, behind a seam so tests never touch the disk.</summary>
public interface IKvmFileSystem
{
    bool FileExists(string path);

    void CreateDirectory(string path);

    Task WriteAllTextAsync(string path, string contents, CancellationToken ct);

    /// <summary>Deletes a directory and everything in it. A missing directory is not an error.</summary>
    void DeleteDirectory(string path);

    /// <summary>True when <paramref name="path"/> is a symbolic link (or other reparse point). Never follows the link.</summary>
    bool IsSymbolicLink(string path);

    /// <summary>Deletes the link at <paramref name="path"/> itself and never anything it points to.</summary>
    void DeleteLink(string path);
}

/// <summary>The real file system.</summary>
public sealed class PhysicalKvmFileSystem : IKvmFileSystem
{
    /// <inheritdoc />
    public bool FileExists(string path) => File.Exists(path);

    /// <inheritdoc />
    public void CreateDirectory(string path) => Directory.CreateDirectory(path);

    /// <inheritdoc />
    public Task WriteAllTextAsync(string path, string contents, CancellationToken ct) => File.WriteAllTextAsync(path, contents, ct);

    /// <inheritdoc />
    public void DeleteDirectory(string path)
    {
        if (Directory.Exists(path))
        {
            if (IsSymbolicLink(path))
            {
                DeleteLink(path);
                return;
            }

            Directory.Delete(path, recursive: true);
        }
    }

    /// <inheritdoc />
    public bool IsSymbolicLink(string path) => new DirectoryInfo(path).LinkTarget is not null;

    /// <inheritdoc />
    public void DeleteLink(string path)
    {
        if (!IsSymbolicLink(path))
        {
            throw new InvalidOperationException($"'{path}' is not a symbolic link.");
        }

        // A directory link is removed without following it: rmdir on Windows, unlink elsewhere.
        if (OperatingSystem.IsWindows())
        {
            Directory.Delete(path, recursive: false);
        }
        else
        {
            File.Delete(path);
        }
    }
}
