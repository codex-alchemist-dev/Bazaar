namespace Bazaar.Server.Vms.Images;

/// <summary>
/// The file operations golden image sealing and verification need. The seam keeps OS-specific read-only handling in one
/// place and lets tests substitute a fake. Verification uses only the read operations.
/// </summary>
public interface IImageFileSystem
{
    /// <summary>True when a file exists at the path.</summary>
    bool FileExists(string path);

    /// <summary>Opens the file for reading. The caller disposes the stream.</summary>
    Stream OpenRead(string path);

    /// <summary>Length of the file in bytes.</summary>
    long GetLength(string path);

    /// <summary>True when no one can write the file.</summary>
    bool IsReadOnly(string path);

    /// <summary>Sets or clears read-only protection on the file.</summary>
    void SetReadOnly(string path, bool isReadOnly);
}
