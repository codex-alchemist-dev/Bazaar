namespace Bazaar.Client.Updater.Linux;

/// <summary>
/// The file operations the updater needs, as a seam so unit tests run without a disk. Paths are absolute.
/// The physical implementation follows Linux semantics (symbolic links, <c>rename(2)</c>).
/// </summary>
public interface IUpdateFileSystem
{
    /// <summary>True when a directory exists at the path (a symbolic link to a directory counts).</summary>
    bool DirectoryExists(string path);

    /// <summary>True when a regular file exists at the path.</summary>
    bool FileExists(string path);

    /// <summary>Creates the directory and any missing parents, with <paramref name="mode"/> on the ones it creates.</summary>
    void CreateDirectory(string path, UnixFileMode mode);

    /// <summary>Opens an existing file for reading.</summary>
    Stream OpenRead(string path);

    /// <summary>Creates a new file for writing, mode owner read and write only. Fails when the path exists.</summary>
    Stream CreateFile(string path);

    /// <summary>Sets the mode of a file or directory.</summary>
    void SetUnixMode(string path, UnixFileMode mode);

    /// <summary>Deletes a file or a symbolic link (never its target). No error when it is absent.</summary>
    void DeleteFile(string path);

    /// <summary>Deletes a directory and everything in it, without following symbolic links. No error when it is absent.</summary>
    void DeleteDirectory(string path);

    /// <summary>The target a symbolic link stores, or null when the path is not a symbolic link.</summary>
    string? ReadLink(string path);

    /// <summary>Creates a symbolic link at <paramref name="path"/> that stores <paramref name="target"/> verbatim. Fails when the path exists.</summary>
    void CreateSymbolicLink(string path, string target);

    /// <summary>Renames <paramref name="source"/> over <paramref name="destination"/> in one atomic step (<c>rename(2)</c>); an existing file or link at <paramref name="destination"/> is replaced.</summary>
    void RenameOver(string source, string destination);

    /// <summary>Renames a directory. Fails when <paramref name="destination"/> exists.</summary>
    void MoveDirectory(string source, string destination);

    /// <summary>Full paths of the immediate subdirectories (links are not followed into).</summary>
    IReadOnlyList<string> EnumerateDirectories(string path);
}
