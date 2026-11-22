namespace Bazaar.Server.SealedPassport;

/// <summary>Link-safe folder walking and zero-wiping for plaintext profile folders. Nothing here follows a symbolic link or reparse point.</summary>
internal static class ProfileFolderWipe
{
    public static bool IsLink(FileSystemInfo info) => info.Exists && (info.LinkTarget is not null || info.Attributes.HasFlag(FileAttributes.ReparsePoint));

    /// <summary>Regular files under <paramref name="root"/>, without descending into links; links are returned separately.</summary>
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

    /// <summary>Deletes links without following them, overwrites each file with zeros, deletes it, and removes the tree. Returns the first failure, or null.</summary>
    public static Exception? WipeAndDelete(string dir)
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
                Try(() =>
                {
                    if (link is DirectoryInfo)
                    {
                        Directory.Delete(link.FullName);
                    }
                    else
                    {
                        File.Delete(link.FullName);
                    }
                }, ref failure);
            }

            foreach (var file in files)
            {
                Try(() =>
                {
                    file.Attributes = FileAttributes.Normal;
                    Zero(file.FullName, file.Length);
                    file.Delete();
                }, ref failure);
            }

            foreach (var d in dirs.OrderByDescending(x => x.FullName.Length))
            {
                Try(d.Delete, ref failure);
            }

            Directory.Delete(dir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            failure ??= ex;
        }

        return failure;
    }

    private static void Try(Action action, ref Exception? failure)
    {
        try
        {
            action();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            failure ??= ex;
        }
    }

    private static void Zero(string path, long length)
    {
        using var fs = new FileStream(path, new FileStreamOptions { Mode = FileMode.Open, Access = FileAccess.Write, Share = FileShare.None, Options = FileOptions.WriteThrough });
        var zeros = new byte[(int)Math.Clamp(length, 1, 81920)];
        for (long done = 0; done < length; done += zeros.Length)
        {
            fs.Write(zeros, 0, (int)Math.Min(zeros.Length, length - done));
        }

        fs.Flush(true);
    }

    /// <summary>Creates a folder only the current user can enter (Unix mode 0700 at creation; on Windows the verified root's ACL is inherited).</summary>
    public static void CreateOwnerOnly(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(path);
        }
        else
        {
            Directory.CreateDirectory(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }
}
