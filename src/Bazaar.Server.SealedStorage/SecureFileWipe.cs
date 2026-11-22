using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Bazaar.Server.SealedStorage;

/// <summary>What <see cref="SecureFileWipe.ZeroAndDelete"/> did with one path.</summary>
public enum WipeOutcome
{
    /// <summary>Nothing was there.</summary>
    Missing,

    /// <summary>A regular file with one link: overwritten with zeros through a no-follow handle, then deleted.</summary>
    Zeroed,

    /// <summary>A link or reparse point: the link itself was deleted, its target never opened.</summary>
    DeletedLink,

    /// <summary>A file with more than one hard link: deleted without zeroing, so the other name's content is not destroyed.</summary>
    DeletedShared,

    /// <summary>The file could not be checked (unsupported platform, or it changed between the check and the open): deleted without zeroing.</summary>
    DeletedUnverified,
}

/// <summary>
/// Link-safe removal of plaintext (doc 61). Walking never descends into a link. A file is zeroed only through a handle opened without following a
/// final link (<c>O_NOFOLLOW</c> on Linux, <c>FILE_FLAG_OPEN_REPARSE_POINT</c> on Windows), after its identity is re-checked on that handle; a file
/// with more than one hard link is deleted without zeroing. Every component between the wipe root and the path is re-checked for links before the
/// open and before the delete. The residual race is a parent folder swapped for a link between that re-check and the system call; the folders this
/// is used on are owner-only, so only the service account can make that swap.
/// </summary>
public static partial class SecureFileWipe
{
    /// <summary>True for an existing symbolic link, junction or other reparse point.</summary>
    public static bool IsLink(FileSystemInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);
        info.Refresh();
        return info.LinkTarget is not null || (info.Exists && info.Attributes.HasFlag(FileAttributes.ReparsePoint));
    }

    /// <summary>True when <paramref name="path"/> exists as a link or reparse point (file or folder form).</summary>
    public static bool IsLink(string path) => IsLink(new FileInfo(path)) || IsLink(new DirectoryInfo(path));

    /// <summary>Walks a folder without descending into links. Returns regular files, links and real folders separately.</summary>
    public static (List<FileInfo> Files, List<FileSystemInfo> Links, List<DirectoryInfo> Dirs) Walk(string root)
    {
        var files = new List<FileInfo>();
        var links = new List<FileSystemInfo>();
        var dirs = new List<DirectoryInfo>();
        var pending = new Stack<DirectoryInfo>();
        pending.Push(new DirectoryInfo(root));
        while (pending.Count > 0)
        {
            foreach (var info in pending.Pop().EnumerateFileSystemInfos("*", new EnumerationOptions { AttributesToSkip = 0, IgnoreInaccessible = false }))
            {
                if (IsLink(info))
                {
                    links.Add(info);
                }
                else if (info is DirectoryInfo d)
                {
                    dirs.Add(d);
                    pending.Push(d);
                }
                else if (info is FileInfo f)
                {
                    files.Add(f);
                }
            }
        }

        return (files, links, dirs);
    }

    /// <summary>
    /// Wipes and removes the folder tree. Refuses (throws <see cref="InvalidOperationException"/>) when <paramref name="dir"/> itself is a link.
    /// Returns the first failure, or null when everything was removed.
    /// </summary>
    public static Exception? WipeTree(string dir) => WipeTree(dir, afterWalk: null);

    /// <summary>As <see cref="WipeTree(string)"/>; <paramref name="afterWalk"/> runs between the walk and the wipe (tests swap paths there).</summary>
    internal static Exception? WipeTree(string dir, Action? afterWalk)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dir);
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dir));
        if (IsLink(root))
        {
            throw new InvalidOperationException("The folder to wipe is a link; it is not followed or wiped.");
        }

        if (!Directory.Exists(root))
        {
            return null;
        }

        Exception? failure = null;
        try
        {
            var (files, links, dirs) = Walk(root);
            afterWalk?.Invoke();
            foreach (var link in links)
            {
                Try(() => DeleteLinkEntry(link.FullName, root), ref failure);
            }

            foreach (var file in files)
            {
                Try(() => ZeroAndDelete(file.FullName, root), ref failure);
            }

            foreach (var d in dirs.OrderByDescending(x => x.FullName.Length))
            {
                Try(() =>
                {
                    EnsureNoLinkBetween(root, d.FullName, includeLast: false);
                    if (IsLink(d.FullName))
                    {
                        DeleteLinkEntry(d.FullName, root);
                    }
                    else if (Directory.Exists(d.FullName))
                    {
                        // Something that appeared after the walk is wiped the same way.
                        if (Directory.EnumerateFileSystemEntries(d.FullName).Any())
                        {
                            failure ??= WipeTree(d.FullName, null);
                        }

                        if (Directory.Exists(d.FullName))
                        {
                            Directory.Delete(d.FullName);
                        }
                    }
                }, ref failure);
            }

            if (Directory.EnumerateFileSystemEntries(root).Any())
            {
                failure ??= new IOException("Entries appeared in the folder while it was being wiped.");
            }
            else
            {
                Directory.Delete(root);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            failure ??= ex;
        }

        return failure;
    }

    /// <summary>
    /// Zeroes and deletes one file below <paramref name="root"/> without following links (see the type remarks). A link is deleted itself; a file
    /// with several hard links, or one that cannot be verified, is deleted without zeroing.
    /// </summary>
    /// <exception cref="InvalidOperationException">A component between <paramref name="root"/> and the file is a link.</exception>
    public static WipeOutcome ZeroAndDelete(string path, string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        var full = Path.GetFullPath(path);
        var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        EnsureNoLinkBetween(fullRoot, full, includeLast: false);
        if (IsLink(full))
        {
            File.Delete(full);
            return WipeOutcome.DeletedLink;
        }

        if (!File.Exists(full))
        {
            return WipeOutcome.Missing;
        }

        var outcome = OperatingSystem.IsLinux() ? ZeroLinux(full)
            : OperatingSystem.IsWindows() ? ZeroWindows(full)
            : WipeOutcome.DeletedUnverified;
        EnsureNoLinkBetween(fullRoot, full, includeLast: false);
        File.Delete(full);
        return outcome;
    }

    private static void DeleteLinkEntry(string path, string root)
    {
        EnsureNoLinkBetween(root, path, includeLast: false);
        if (new DirectoryInfo(path) is { } d && IsLink(d) && d.Exists)
        {
            // Removes the link (or junction) itself; the target is never entered.
            d.Delete();
        }
        else
        {
            File.Delete(path);
        }
    }

    /// <summary>Throws when any existing component strictly below <paramref name="root"/> on the way to <paramref name="path"/> is a link.</summary>
    private static void EnsureNoLinkBetween(string root, string path, bool includeLast)
    {
        if (IsLink(root))
        {
            throw new InvalidOperationException("The wipe root is a link; nothing below it is touched.");
        }

        var relative = Path.GetRelativePath(root, path);
        if (relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative))
        {
            throw new InvalidOperationException("The path is not inside the wipe root.");
        }

        var parts = relative.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        var current = root;
        for (var i = 0; i < parts.Length - (includeLast ? 0 : 1); i++)
        {
            current = Path.Combine(current, parts[i]);
            if (IsLink(current))
            {
                throw new InvalidOperationException("A folder on the wipe path is a link; nothing below it is touched.");
            }
        }
    }

    private static void Try(Action action, ref Exception? failure)
    {
        try
        {
            action();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            failure ??= ex;
        }
    }

    private static void WriteZeros(SafeFileHandle handle, long length)
    {
        using var fs = new FileStream(handle, FileAccess.Write, 1);
        var zeros = new byte[(int)Math.Clamp(length, 1, 81920)];
        for (long done = 0; done < length; done += zeros.Length)
        {
            fs.Write(zeros, 0, (int)Math.Min(zeros.Length, length - done));
        }

        fs.Flush(true);
    }

    // ---- Linux: statx (layout is the same on every architecture) and open(O_NOFOLLOW).
    private const int _atFdCwd = -100;
    private const int _atSymlinkNoFollow = 0x100;
    private const int _atEmptyPath = 0x1000;
    private const uint _statxBasicStats = 0x7ff;
    private const int _oWriteOnly = 0x1;
    private const int _oCloseOnExec = 0x80000;

    private readonly record struct UnixIdentity(uint Mode, uint Links, ulong Inode, uint DevMajor, uint DevMinor, long Size)
    {
        public bool IsRegular => (Mode & 0xF000) == 0x8000;

        public bool SameFile(UnixIdentity other) => Inode == other.Inode && DevMajor == other.DevMajor && DevMinor == other.DevMinor;
    }

    private static int? NoFollowFlag() => RuntimeInformation.ProcessArchitecture switch
    {
        Architecture.X64 => 0x20000,
        Architecture.X86 => 0x20000,
        Architecture.Arm64 => 0x8000,
        Architecture.Arm => 0x8000,
        _ => null,
    };

    private static UnixIdentity? Identity(int dirFd, string path, int flags)
    {
        var buffer = new byte[256];
        if (Statx(dirFd, path, flags, _statxBasicStats, buffer) != 0)
        {
            return null;
        }

        return new UnixIdentity(
            BinaryPrimitives.ReadUInt16LittleEndian(buffer.AsSpan(28)),
            BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(16)),
            BinaryPrimitives.ReadUInt64LittleEndian(buffer.AsSpan(32)),
            BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(136)),
            BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(140)),
            (long)BinaryPrimitives.ReadUInt64LittleEndian(buffer.AsSpan(40)));
    }

    private static WipeOutcome ZeroLinux(string path)
    {
        try
        {
            if (NoFollowFlag() is not { } noFollow || !BitConverter.IsLittleEndian)
            {
                return WipeOutcome.DeletedUnverified;
            }

            var before = Identity(_atFdCwd, path, _atSymlinkNoFollow);
            if (before is not { IsRegular: true } pre)
            {
                return WipeOutcome.DeletedUnverified;
            }

            var fd = Open(path, _oWriteOnly | noFollow | _oCloseOnExec);
            if (fd < 0)
            {
                // ELOOP: a link was swapped in after the check. The link is deleted by the caller; its target is never opened.
                return WipeOutcome.DeletedUnverified;
            }

            using var handle = new SafeFileHandle(fd, ownsHandle: true);
            var after = Identity(fd, string.Empty, _atEmptyPath);
            if (after is not { IsRegular: true } post || !post.SameFile(pre))
            {
                return WipeOutcome.DeletedUnverified;
            }

            if (post.Links != 1)
            {
                return WipeOutcome.DeletedShared;
            }

            WriteZeros(handle, post.Size);
            return WipeOutcome.Zeroed;
        }
        catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException)
        {
            return WipeOutcome.DeletedUnverified;
        }
    }

    [LibraryImport("libc", EntryPoint = "statx", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int Statx(int dirFd, string path, int flags, uint mask, [Out] byte[] buffer);

    [LibraryImport("libc", EntryPoint = "open", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int Open(string path, int flags);

    // ---- Windows: CreateFileW(FILE_FLAG_OPEN_REPARSE_POINT) and GetFileInformationByHandle.
    private const uint _genericWrite = 0x40000000;
    private const uint _fileReadAttributes = 0x80;
    private const uint _openExisting = 3;
    private const uint _flagOpenReparsePoint = 0x00200000;
    private const uint _attributeReparsePoint = 0x400;

    private static WipeOutcome ZeroWindows(string path)
    {
        try
        {
            using var handle = CreateFile(path, _genericWrite | _fileReadAttributes, 0, IntPtr.Zero, _openExisting, _flagOpenReparsePoint, IntPtr.Zero);
            if (handle.IsInvalid || !GetFileInformationByHandle(handle, out var info))
            {
                return WipeOutcome.DeletedUnverified;
            }

            if ((info.FileAttributes & _attributeReparsePoint) != 0)
            {
                return WipeOutcome.DeletedUnverified;
            }

            if (info.NumberOfLinks != 1)
            {
                return WipeOutcome.DeletedShared;
            }

            WriteZeros(handle, ((long)info.FileSizeHigh << 32) | info.FileSizeLow);
            return WipeOutcome.Zeroed;
        }
        catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException)
        {
            return WipeOutcome.DeletedUnverified;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public long CreationTime;
        public long LastAccessTime;
        public long LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);

    [LibraryImport("kernel32.dll", EntryPoint = "GetFileInformationByHandle", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetFileInformationByHandle(SafeFileHandle handle, out ByHandleFileInformation info);
}
