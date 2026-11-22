namespace Bazaar.Server.DataVolume;

/// <summary>
/// Checks that a path stays inside the data root with no symbolic link, junction or nested mount point on the way
/// (doc 63 section 5, step 3). PostgreSQL's data directory, <c>pg_wal</c> and tablespaces go through it, so database
/// files cannot sit on a plain volume behind a link.
/// </summary>
public static class DataRootPaths
{
    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <exception cref="DataVolumeRefusedException">A path is outside the data root or reaches it through a link.</exception>
    public static void EnsureInside(string dataRoot, IEnumerable<string> paths)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        ArgumentNullException.ThrowIfNull(paths);
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dataRoot));

        foreach (var path in paths)
        {
            if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
                throw Outside(path, "it is not an absolute path");

            var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
            if (!full.Equals(root, PathComparison) && !full.StartsWith(root + Path.DirectorySeparatorChar, PathComparison))
                throw Outside(path, "it is not under the data root");

            // Walk down from the data root. The root itself is a mount point by design and is verified by the provider.
            var current = root;
            foreach (var segment in full[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
            {
                current = Path.Combine(current, segment);
                if (IsLink(current))
                    throw Outside(path, $"'{current}' is a link or a mount point");
            }
        }
    }

    private static bool IsLink(string path)
    {
        FileSystemInfo info = Directory.Exists(path) ? new DirectoryInfo(path) : new FileInfo(path);
        if (!info.Exists)
            return false;

        return info.LinkTarget is not null || info.Attributes.HasFlag(FileAttributes.ReparsePoint);
    }

    private static DataVolumeRefusedException Outside(string? path, string why) =>
        new(DataVolumeRefusal.PostgresFilesOutsideVolume, $"Path '{path}' is refused: {why}. Every database file must be inside the encrypted data root (doc 63).");
}
