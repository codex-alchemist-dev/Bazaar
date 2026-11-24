using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace Bazaar.Server.SealedGit;

/// <summary>Owner-only temp folders, link-safe walking and wiping. Nothing here follows a symlink or reparse point.</summary>
internal static partial class SecureTemp
{
    internal const string TempPrefix = "bazaar-sealedgit-";

    /// <summary>Creates a folder only the current user can enter, in one step (mode or ACL set at creation).</summary>
    internal static void CreateOwnerOnly(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            CreateWindowsOwnerOnly(path);
        }
        else
        {
            Directory.CreateDirectory(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    [SupportedOSPlatform("windows")]
    private static void CreateWindowsOwnerOnly(string path)
    {
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        var sid = WindowsIdentity.GetCurrent().User!;
        security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(path).Create(security);
    }

    internal static bool IsLink(FileSystemInfo info) => info.Exists && (info.Attributes.HasFlag(FileAttributes.ReparsePoint) || info.LinkTarget is not null);

    /// <summary>Walks a folder without descending into links. Returns files, links and directories separately.</summary>
    internal static (List<FileInfo> Files, List<FileSystemInfo> Links, List<DirectoryInfo> Dirs) Walk(string root)
    {
        var files = new List<FileInfo>();
        var links = new List<FileSystemInfo>();
        var dirs = new List<DirectoryInfo>();
        var pending = new Stack<DirectoryInfo>();
        pending.Push(new DirectoryInfo(root));
        while (pending.Count > 0)
        {
            var dir = pending.Pop();
            foreach (var info in dir.EnumerateFileSystemInfos("*", new EnumerationOptions { AttributesToSkip = 0, IgnoreInaccessible = false }))
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

    /// <summary>Zero-wipes regular files with a single link, deletes links without following them, and removes the tree. Returns the failure, or null.</summary>
    internal static Exception? WipeAndDelete(string dir)
    {
        if (!Directory.Exists(dir))
        {
            return null;
        }

        Exception? failure = null;
        try
        {
            var (files, links, dirs) = Walk(dir);
            foreach (var link in links)
            {
                try
                {
                    if (link is DirectoryInfo)
                    {
                        Directory.Delete(link.FullName);
                    }
                    else
                    {
                        File.Delete(link.FullName);
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    failure ??= ex;
                }
            }

            foreach (var file in files)
            {
                try
                {
                    file.Attributes = FileAttributes.Normal;
                    if (LinkCount(file.FullName) == 1)
                    {
                        ZeroInPlace(file.FullName, file.Length);
                    }

                    file.Delete();
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    failure ??= ex;
                }
            }

            foreach (var d in dirs.OrderByDescending(x => x.FullName.Length))
            {
                try
                {
                    d.Delete();
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    failure ??= ex;
                }
            }

            Directory.Delete(dir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            failure ??= ex;
        }

        return failure;
    }

    private static void ZeroInPlace(string path, long length)
    {
        using var fs = new FileStream(path, new FileStreamOptions { Mode = FileMode.Open, Access = FileAccess.Write, Share = FileShare.None, Options = FileOptions.WriteThrough });
        var zeros = new byte[(int)Math.Clamp(length, 1, 81920)];
        for (long done = 0; done < length; done += zeros.Length)
        {
            fs.Write(zeros, 0, (int)Math.Min(zeros.Length, length - done));
        }

        fs.Flush(true);
    }

    /// <summary>Number of hard links to the file; returns int.MaxValue (treated as shared) when it cannot be determined.</summary>
    internal static int LinkCount(string path)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                return GetFileInformationByHandle(fs.SafeFileHandle, out var info) ? (int)info.NumberOfLinks : int.MaxValue;
            }

            var buffer = new byte[512];
            if (Stat(path, buffer) != 0)
            {
                return int.MaxValue;
            }

            return RuntimeInformation.ProcessArchitecture switch
            {
                Architecture.X64 when OperatingSystem.IsLinux() => (int)BitConverter.ToUInt64(buffer, 16),
                Architecture.Arm64 when OperatingSystem.IsLinux() => (int)BitConverter.ToUInt32(buffer, 20),
                _ => int.MaxValue,
            };
        }
        catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException or IOException or UnauthorizedAccessException)
        {
            return int.MaxValue;
        }
    }

    /// <summary>Wipes leftover folders of earlier crashed runs. Call only when no operation of this process is running in <paramref name="tempRoot"/>.</summary>
    internal static List<Exception> SweepStale(string tempRoot)
    {
        var failures = new List<Exception>();
        if (!Directory.Exists(tempRoot))
        {
            return failures;
        }

        foreach (var stale in Directory.EnumerateDirectories(tempRoot, TempPrefix + "*"))
        {
            if (WipeAndDelete(stale) is { } failure)
            {
                failures.Add(failure);
            }
        }

        return failures;
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

    [LibraryImport("kernel32.dll", EntryPoint = "GetFileInformationByHandle", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetFileInformationByHandle(SafeFileHandle handle, out ByHandleFileInformation info);

    [LibraryImport("libc", EntryPoint = "stat", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int Stat(string path, [Out] byte[] buffer);
}
