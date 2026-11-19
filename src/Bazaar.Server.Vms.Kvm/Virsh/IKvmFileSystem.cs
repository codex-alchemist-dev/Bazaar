namespace Bazaar.Server.Vms.Kvm.Virsh;

/// <summary>The few file operations the virsh adapter needs, behind a seam so tests never touch the disk.</summary>
public interface IKvmFileSystem
{
    bool FileExists(string path);

    void CreateDirectory(string path);

    Task WriteAllTextAsync(string path, string contents, CancellationToken ct);

    /// <summary>Deletes a directory and everything in it. A missing directory is not an error.</summary>
    void DeleteDirectory(string path);
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
            Directory.Delete(path, recursive: true);
        }
    }
}
