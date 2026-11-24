namespace Bazaar.Server.Identity.Mount;

/// <summary>
/// The storage the mounted folder writes to. Paths are relative to the mount root, use '/' as the separator, and
/// must not contain traversal segments. Implementations refuse anything that escapes the root.
/// </summary>
public interface IMountFileSystem
{
    /// <summary>Creates or replaces a file.</summary>
    void WriteFile(string relativePath, ReadOnlyMemory<byte> content);

    /// <summary>Deletes a file if it exists.</summary>
    void DeleteFile(string relativePath);

    /// <summary>Lists every file under a directory (recursively), as relative paths. An empty directory means the mount root.</summary>
    IReadOnlyList<string> ListFiles(string directory);

    /// <summary>Whether a file exists.</summary>
    bool Exists(string relativePath);

    /// <summary>Removes a directory and everything in it. Refuses the mount root itself.</summary>
    void Detach(string directory);
}
