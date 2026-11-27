namespace Bazaar.Server.SealedGit;

/// <summary>
/// Path rules shared by the sealed git mount and the sealed Vault (doc 61 "Vault adoption"). Containment is checked on canonical paths: full,
/// trimmed, with every existing link component resolved to its final target, so an alias cannot move a path into or out of the mount root.
/// </summary>
public static class SealedPaths
{
    /// <summary>Ordinal on Linux; ignoring case on Windows and macOS, whose default file systems do.</summary>
    public static StringComparison Comparison =>
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>
    /// The canonical form of <paramref name="path"/>: rejects device (<c>\\?\</c>, <c>\\.\</c>) and UNC forms, makes it full, and resolves every
    /// existing component that is a link (symbolic link, junction) to its final target; components that do not exist yet are appended as given.
    /// </summary>
    public static string Canonicalize(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        RejectSpecialForms(path);
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        RejectSpecialForms(full);

        var root = Path.GetPathRoot(full) ?? throw new InvalidOperationException($"'{path}' has no root.");
        var current = root;
        var parts = full[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < parts.Length; i++)
        {
            var next = Path.Combine(current, parts[i]);
            FileSystemInfo info = new DirectoryInfo(next);
            if (!info.Exists)
            {
                info = new FileInfo(next);
            }

            if (!info.Exists && info.LinkTarget is null)
            {
                // The rest does not exist yet: nothing more to resolve.
                return Path.TrimEndingDirectorySeparator(Path.Combine([current, .. parts[i..]]));
            }

            if (info.LinkTarget is not null)
            {
                var target = info.ResolveLinkTarget(returnFinalTarget: true)?.FullName
                    ?? throw new InvalidOperationException($"The link '{next}' cannot be resolved.");
                next = Canonicalize(target);
            }

            current = next;
        }

        return Path.TrimEndingDirectorySeparator(current);
    }

    /// <summary>True when any existing component of the full path (the path itself included) is a link.</summary>
    public static bool HasLinkComponent(string path)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        for (var current = full; !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
        {
            FileSystemInfo info = new DirectoryInfo(current);
            if (!info.Exists)
            {
                info = new FileInfo(current);
            }

            if (info.LinkTarget is not null || (info.Exists && info.Attributes.HasFlag(FileAttributes.ReparsePoint)))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>True when the canonical paths are equal.</summary>
    public static bool PathEquals(string a, string b) => string.Equals(a, b, Comparison);

    /// <summary>True when the canonical path <paramref name="path"/> is strictly below the canonical path <paramref name="parent"/> (a prefix sibling such as <c>/mnt-x</c> of <c>/mnt</c> is not).</summary>
    public static bool IsStrictlyInside(string path, string parent)
    {
        var prefix = Path.TrimEndingDirectorySeparator(parent) + Path.DirectorySeparatorChar;
        var trimmed = Path.TrimEndingDirectorySeparator(path);
        return trimmed.Length > prefix.Length && trimmed.StartsWith(prefix, Comparison);
    }

    /// <summary>True when the canonical path is equal to or below the canonical parent.</summary>
    public static bool IsAtOrInside(string path, string parent) => PathEquals(path, parent) || IsStrictlyInside(path, parent);

    private static void RejectSpecialForms(string path)
    {
        if (path.StartsWith(@"\\", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal)
            || path.StartsWith(@"\??\", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Device and UNC paths are not allowed for sealed mounts ('{path}').");
        }
    }
}
