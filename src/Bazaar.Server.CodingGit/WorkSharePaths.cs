namespace Bazaar.Server.CodingGit;

/// <summary>
/// Canonical path handling for the work share and Passport folder rule. Resolves <c>..</c>, symbolic links and
/// junctions on every existing component, so an alias cannot hide a path under the Passport folder.
/// </summary>
public static class WorkSharePaths
{
    private const int _maxLinkHops = 40;

    /// <summary>The comparison for the host file system: case-insensitive on Windows and macOS.</summary>
    public static StringComparison Comparison =>
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>Returns the absolute path with every link resolved. Components that do not exist yet are kept as written.</summary>
    /// <exception cref="CodingGitException">The path is blank, contains NUL, or has a link loop.</exception>
    public static string Canonicalize(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Contains('\0'))
        {
            throw new CodingGitException(CodingGitError.InvalidRequest, "A path is blank or malformed.");
        }

        var full = Path.GetFullPath(path);
        var root = Path.GetPathRoot(full) ?? string.Empty;
        var pending = new Stack<string>(Split(full, root).Reverse());
        var current = root;
        var hops = 0;
        while (pending.Count > 0)
        {
            var segment = pending.Pop();
            var next = Path.Combine(current, segment);
            var target = new DirectoryInfo(next).LinkTarget;
            if (target is null)
            {
                current = next;
                continue;
            }

            if (++hops > _maxLinkHops)
            {
                throw new CodingGitException(CodingGitError.InvalidRequest, "A path has too many nested links.");
            }

            var resolved = Path.GetFullPath(Path.IsPathRooted(target) ? target : Path.Combine(current, target));
            current = Path.GetPathRoot(resolved) ?? root;
            foreach (var part in Split(resolved, current).Reverse())
            {
                pending.Push(part);
            }
        }

        return current;
    }

    /// <summary>True when <paramref name="child"/> equals or is below <paramref name="parent"/>. Both must be canonical.</summary>
    public static bool IsEqualOrUnder(string child, string parent)
    {
        var c = child.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var p = parent.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return string.Equals(c, p, Comparison) || c.StartsWith(p + Path.DirectorySeparatorChar, Comparison);
    }

    private static string[] Split(string full, string root) =>
        full[root.Length..].Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
}
