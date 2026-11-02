namespace Bazaar.Server.Vms.Images;

/// <summary>
/// The real file system. Read-only means the Windows read-only attribute on Windows, and no write bits for any user on
/// other systems. Both branches are chosen at run time, so the code compiles and runs on every OS.
/// </summary>
public sealed class PhysicalImageFileSystem : IImageFileSystem
{
    private const UnixFileMode _writeBits = UnixFileMode.UserWrite | UnixFileMode.GroupWrite | UnixFileMode.OtherWrite;

    public bool FileExists(string path) => File.Exists(path);

    public Stream OpenRead(string path) => File.OpenRead(path);

    public long GetLength(string path) => new FileInfo(path).Length;

    public bool IsReadOnly(string path)
    {
        if (OperatingSystem.IsWindows())
            return (File.GetAttributes(path) & FileAttributes.ReadOnly) != 0;

        return (File.GetUnixFileMode(path) & _writeBits) == 0;
    }

    public void SetReadOnly(string path, bool isReadOnly)
    {
        if (OperatingSystem.IsWindows())
        {
            var attributes = File.GetAttributes(path);
            var updated = isReadOnly ? attributes | FileAttributes.ReadOnly : attributes & ~FileAttributes.ReadOnly;
            if (updated != attributes)
                File.SetAttributes(path, updated);
            return;
        }

        var mode = File.GetUnixFileMode(path);
        var changed = isReadOnly ? mode & ~_writeBits : mode | UnixFileMode.UserWrite;
        if (changed != mode)
            File.SetUnixFileMode(path, changed);
    }
}
