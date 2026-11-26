using Bazaar.Contracts.Vms;

namespace Bazaar.Guest.Agent;

/// <summary>
/// Resolves guest paths against the allowed roots the way realpath(3) does: segment by segment, applying <c>..</c> to the
/// path resolved so far and following every symlink on the way, so a link inside a root that points outside it is
/// refused. Every failure to look at the file system fails closed (null).
/// </summary>
internal sealed class PathGuard
{
    private const int _maxLinkHops = 40;
    private readonly string[] _roots;

    /// <summary>Creates a guard over the given roots. The first root is the worktree root that relative paths use.</summary>
    public PathGuard(IEnumerable<string> roots)
    {
        var all = roots.ToArray();
        _roots = all
            .Select(root => SafeRealPath(Path.GetFullPath(root)))
            .OfType<string>()
            .ToArray();
        PrimaryRoot = all.Length > 0 ? SafeRealPath(Path.GetFullPath(all[0])) : null;
    }

    /// <summary>The real path of the first configured root, or null when none is configured or it cannot be resolved.</summary>
    public string? PrimaryRoot { get; }

    /// <summary>The root-relative path rules with this OS's strictness.</summary>
    public static bool IsSafeRelative(string? path, bool allowRoot) =>
        GuestPathRules.IsSafeRelative(path, allowRoot, OperatingSystem.IsWindows());

    /// <summary>
    /// Resolves a root-relative path against the primary root, following every symlink. Returns the real path when it lies
    /// inside the primary root (the root itself only with <paramref name="includeRoot"/>), otherwise null. The path need not exist.
    /// </summary>
    public string? ResolveRelative(string path, bool includeRoot)
    {
        if (PrimaryRoot is null || !IsSafeRelative(path, includeRoot))
        {
            return null;
        }

        var real = SafeRealPath(Path.Combine(PrimaryRoot, path.Replace('\\', '/')));
        return real is not null && IsUnder(real, PrimaryRoot, includeRoot) ? real : null;
    }

    /// <summary>Turns a real path inside the primary root into a root-relative path with <c>/</c> separators; <c>.</c> is the root.</summary>
    public string ToRelative(string realPath) =>
        Path.GetRelativePath(PrimaryRoot!, realPath).Replace(Path.DirectorySeparatorChar, '/');

    /// <summary>Returns the real path when it lies strictly inside a root (a file path), otherwise null.</summary>
    public string? Resolve(string path) => Locate(path, includeRoot: false);

    /// <summary>Returns the real path when it is a root or lies inside one (a working directory), otherwise null.</summary>
    public string? ResolveDirectory(string path) => Locate(path, includeRoot: true);

    private string? Locate(string path, bool includeRoot)
    {
        if (string.IsNullOrEmpty(path) || !Path.IsPathFullyQualified(path))
        {
            return null;
        }

        var real = SafeRealPath(path);
        return real is not null && _roots.Any(root => IsUnder(real, root, includeRoot)) ? real : null;
    }

    private static string? SafeRealPath(string fullPath)
    {
        try
        {
            return RealPath(fullPath);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>
    /// realpath(3): the segments still to walk sit on a work list, a link target is pushed onto the front of it, and
    /// <c>..</c> is applied to the directory resolved so far, never to the text of the path.
    /// </summary>
    private static string? RealPath(string fullPath)
    {
        if (fullPath.Contains('\0'))
        {
            return null;
        }

        var root = Path.GetPathRoot(fullPath) ?? string.Empty;
        var current = root;
        var work = new LinkedList<string>(Split(fullPath[root.Length..]));
        var hops = 0;
        while (work.First is { } node)
        {
            work.RemoveFirst();
            var part = node.Value;
            if (part == ".")
            {
                continue;
            }

            if (part == "..")
            {
                current = current.Length <= root.Length ? current : Path.GetDirectoryName(current) ?? root;
                continue;
            }

            var candidate = Path.Combine(current, part);
            var target = new FileInfo(candidate).LinkTarget;
            if (target is null)
            {
                current = candidate;
                continue;
            }

            if (++hops > _maxLinkHops)
            {
                return null;
            }

            var targetRoot = string.Empty;
            if (Path.IsPathFullyQualified(target))
            {
                targetRoot = Path.GetPathRoot(target) ?? string.Empty;
                current = targetRoot;
                root = targetRoot;
            }
            else if (target.StartsWith('/') || target.StartsWith('\\'))
            {
                // Rooted without a volume: the volume of the directory that holds the link.
                targetRoot = target[..1];
                current = root;
            }
            else if (Path.IsPathRooted(target))
            {
                return null; // A drive-relative target such as C:foo has no stable meaning.
            }

            foreach (var segment in Split(target[targetRoot.Length..]).Reverse())
            {
                work.AddFirst(segment);
            }
        }

        return current;
    }

    private static string[] Split(string path) =>
        path.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);

    private static bool IsUnder(string path, string root, bool includeRoot)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (path.Equals(root, comparison))
        {
            return includeRoot;
        }

        var prefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        return path.Length > prefix.Length && path.StartsWith(prefix, comparison);
    }
}
