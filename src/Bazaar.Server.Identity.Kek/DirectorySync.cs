using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Bazaar.Server.Identity.Kek;

/// <summary>
/// Flushes a directory's entries to disk after a rename, so the new name survives a power loss (POSIX; NTFS journals renames and has no
/// directory flush). Best effort: a file system that cannot sync a directory is not an error.
/// </summary>
internal static partial class DirectorySync
{
    private const int _readOnlyCloseOnExec = 0x80000; // O_RDONLY | O_CLOEXEC, the same value on every Linux architecture.

    public static void Flush(string directory)
    {
        if (OperatingSystem.IsWindows() || !OperatingSystem.IsLinux())
        {
            return;
        }

        try
        {
            using var handle = Open(directory, _readOnlyCloseOnExec);
            if (!handle.IsInvalid)
            {
                RandomAccess.FlushToDisk(handle);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or EntryPointNotFoundException or DllNotFoundException)
        {
            // Best effort by design.
        }
    }

    [LibraryImport("libc", EntryPoint = "open", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial SafeFileHandle Open(string path, int flags);
}
