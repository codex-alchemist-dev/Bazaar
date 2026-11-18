using System.Runtime.InteropServices;

namespace Bazaar.Server.Backup.Archiving;

/// <summary>
/// fsync on a directory handle. .NET's file open refuses directories on POSIX, so the flush calls libc directly.
/// Only compiled into the POSIX path; Windows does not need a directory flush.
/// </summary>
internal static partial class PosixDirectorySync
{
    private const int _oReadOnly = 0;

    [LibraryImport("libc", EntryPoint = "open", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int Open(string path, int flags);

    [LibraryImport("libc", EntryPoint = "fsync", SetLastError = true)]
    private static partial int FSync(int fd);

    [LibraryImport("libc", EntryPoint = "close", SetLastError = true)]
    private static partial int Close(int fd);

    public static void Flush(string directory)
    {
        var fd = Open(directory, _oReadOnly);
        if (fd < 0)
            throw new IOException($"Cannot open {directory} for fsync (errno {Marshal.GetLastPInvokeError()}).");
        try
        {
            if (FSync(fd) != 0)
                throw new IOException($"fsync of {directory} failed (errno {Marshal.GetLastPInvokeError()}).");
        }
        finally
        {
            Close(fd);
        }
    }
}
