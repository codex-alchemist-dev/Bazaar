using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Bazaar.Server.SealedBootstrap;

/// <summary>Who a file is: its volume and file number, its hard link count and whether it is a regular file (not a link, device or directory).</summary>
internal readonly record struct FileIdentity(ulong Device, ulong Inode, ulong Links, bool IsRegularFile)
{
    /// <summary>True when both name the same file on the same volume.</summary>
    public bool IsSameFile(FileIdentity other) => Device == other.Device && Inode == other.Inode;
}

/// <summary>
/// Reads a file's identity from an open handle (fstat; <c>GetFileInformationByHandle</c> on Windows) or from a path without following a
/// final link (lstat), so an import can prove that the file it read and zero-filled is the file it is about to delete, and that nothing
/// else is a hard link to it. A platform that cannot answer returns null and the caller fails closed. Linux uses <c>statx</c>, whose
/// layout is the same on every architecture.
/// </summary>
internal static partial class FileIdentityReader
{
    private const int _atFdCwd = -100;
    private const int _atSymlinkNoFollow = 0x100;
    private const int _atEmptyPath = 0x1000;
    private const uint _statxBasicStats = 0x7ff;
    private const int _statxBufferBytes = 256;
    private const uint _modeTypeMask = 0xF000;
    private const uint _modeRegular = 0x8000;

    public static FileIdentity? FromHandle(SafeFileHandle handle)
    {
        ArgumentNullException.ThrowIfNull(handle);
        try
        {
            if (OperatingSystem.IsWindows())
            {
                return GetFileInformationByHandle(handle, out var info) ? FromWindows(info) : null;
            }

            if (OperatingSystem.IsLinux())
            {
                var buffer = new byte[_statxBufferBytes];
                var fd = (int)handle.DangerousGetHandle();
                var result = Statx(fd, string.Empty, _atEmptyPath, _statxBasicStats, buffer);
                GC.KeepAlive(handle);
                return result == 0 ? FromStatx(buffer) : null;
            }
        }
        catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException)
        {
            return null;
        }

        return null;
    }

    public static FileIdentity? FromPath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        try
        {
            if (OperatingSystem.IsWindows())
            {
                // Reparse points were refused before the open; a plain open here names the same file or fails.
                using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                return FromHandle(handle);
            }

            if (OperatingSystem.IsLinux())
            {
                var buffer = new byte[_statxBufferBytes];
                return Statx(_atFdCwd, path, _atSymlinkNoFollow, _statxBasicStats, buffer) == 0 ? FromStatx(buffer) : null;
            }
        }
        catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException or IOException or UnauthorizedAccessException)
        {
            return null;
        }

        return null;
    }

    private static FileIdentity FromStatx(byte[] buffer)
    {
        var links = BitConverter.ToUInt32(buffer, 16);
        var mode = BitConverter.ToUInt16(buffer, 28);
        var inode = BitConverter.ToUInt64(buffer, 32);
        var deviceMajor = BitConverter.ToUInt32(buffer, 136);
        var deviceMinor = BitConverter.ToUInt32(buffer, 140);
        return new FileIdentity(((ulong)deviceMajor << 32) | deviceMinor, inode, links, (mode & _modeTypeMask) == _modeRegular);
    }

    private static FileIdentity FromWindows(ByHandleFileInformation info) =>
        new(info.VolumeSerialNumber, ((ulong)info.FileIndexHigh << 32) | info.FileIndexLow, info.NumberOfLinks, (info.FileAttributes & 0x400) == 0);

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public uint CreationTimeLow;
        public uint CreationTimeHigh;
        public uint LastAccessTimeLow;
        public uint LastAccessTimeHigh;
        public uint LastWriteTimeLow;
        public uint LastWriteTimeHigh;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "GetFileInformationByHandle", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetFileInformationByHandle(SafeFileHandle handle, out ByHandleFileInformation info);

    [LibraryImport("libc", EntryPoint = "statx", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int Statx(int directoryFd, string path, int flags, uint mask, [Out] byte[] buffer);
}
