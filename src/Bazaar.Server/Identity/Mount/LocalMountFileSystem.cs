namespace Bazaar.Server.Identity.Mount;

/// <summary>
/// An <see cref="IMountFileSystem"/> that writes under a root directory on the local disk. It works on Windows and
/// Linux with the portable <see cref="Path"/> and <see cref="File"/> APIs.
/// </summary>
/// <remarks>
/// Guards: the root must be absolute, must not be a filesystem root, and must not overlap any forbidden root (for
/// example a coding worktree). Every path is checked to stay under the root, and links inside the root are refused so
/// a write cannot follow a link out of it. The root is refused at construction, not at write time.
/// </remarks>
public sealed class LocalMountFileSystem : IMountFileSystem
{
    private readonly string _root;
    private readonly StringComparison _comparison;

    /// <summary>
    /// Creates a mount rooted at <paramref name="root"/>.
    /// </summary>
    /// <param name="root">Absolute directory the mount writes under.</param>
    /// <param name="forbiddenOverlapRoots">Directories the mount must not overlap, such as agent coding worktrees.</param>
    /// <exception cref="InvalidOperationException">The root equals, contains or is contained by a forbidden root.</exception>
    public LocalMountFileSystem(string root, IReadOnlyCollection<string> forbiddenOverlapRoots)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentNullException.ThrowIfNull(forbiddenOverlapRoots);

        if (!Path.IsPathRooted(root))
        {
            throw new ArgumentException("The mount root must be an absolute path.", nameof(root));
        }

        _comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        _root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));

        if (string.Equals(_root, Path.GetPathRoot(_root), _comparison))
        {
            throw new ArgumentException("The mount root cannot be a filesystem root.", nameof(root));
        }

        foreach (var forbidden in forbiddenOverlapRoots)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(forbidden);
            var forbiddenFull = Path.TrimEndingDirectorySeparator(Path.GetFullPath(forbidden));
            if (Overlaps(_root, forbiddenFull))
            {
                throw new InvalidOperationException($"The mount root overlaps a forbidden root and was refused: {forbidden}");
            }
        }
    }

    /// <summary>The absolute mount root.</summary>
    public string Root => _root;

    /// <inheritdoc />
    public void WriteFile(string relativePath, ReadOnlyMemory<byte> content)
    {
        var full = ResolvePath(relativePath, allowEmpty: false);
        var directory = Path.GetDirectoryName(full);
        if (directory is not null)
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllBytes(full, content.ToArray());

        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(full, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    /// <inheritdoc />
    public void DeleteFile(string relativePath)
    {
        var full = ResolvePath(relativePath, allowEmpty: false);
        if (File.Exists(full))
        {
            File.Delete(full);
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<string> ListFiles(string directory)
    {
        var full = ResolvePath(directory, allowEmpty: true);
        if (!Directory.Exists(full))
        {
            return [];
        }

        return Directory.EnumerateFiles(full, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(_root, f).Replace(Path.DirectorySeparatorChar, '/'))
            .Order(StringComparer.Ordinal)
            .ToList();
    }

    /// <inheritdoc />
    public bool Exists(string relativePath) => File.Exists(ResolvePath(relativePath, allowEmpty: false));

    /// <inheritdoc />
    public void Detach(string directory)
    {
        var full = ResolvePath(directory, allowEmpty: false);
        if (Directory.Exists(full))
        {
            Directory.Delete(full, recursive: true);
        }
    }

    private bool Overlaps(string a, string b) =>
        string.Equals(a, b, _comparison) || IsWithin(a, b) || IsWithin(b, a);

    private bool IsWithin(string path, string directory)
    {
        var prefix = directory.EndsWith(Path.DirectorySeparatorChar) ? directory : directory + Path.DirectorySeparatorChar;
        return path.Length > prefix.Length && path.StartsWith(prefix, _comparison);
    }

    /// <summary>Maps a relative path to a full path under the root, refusing traversal and links inside the root.</summary>
    private string ResolvePath(string relativePath, bool allowEmpty)
    {
        if (string.IsNullOrEmpty(relativePath))
        {
            if (allowEmpty)
            {
                return _root;
            }

            throw new ArgumentException("A relative path is required.", nameof(relativePath));
        }

        if (Path.IsPathRooted(relativePath) || relativePath.Contains('\\', StringComparison.Ordinal))
        {
            throw new ArgumentException("Mount paths must be relative and use '/' separators.", nameof(relativePath));
        }

        var segments = relativePath.Split('/');
        var current = _root;
        foreach (var segment in segments)
        {
            if (segment.Length == 0 || segment == "." || segment == "..")
            {
                throw new ArgumentException("Mount paths may not contain empty or traversal segments.", nameof(relativePath));
            }

            current = Path.Combine(current, segment);
            RefuseLink(current);
        }

        if (!IsWithin(current, _root))
        {
            throw new InvalidOperationException("The resolved mount path escaped the mount root.");
        }

        return current;
    }

    private static void RefuseLink(string path)
    {
        if ((File.Exists(path) || Directory.Exists(path)) && File.ResolveLinkTarget(path, returnFinalTarget: false) is not null)
        {
            throw new InvalidOperationException("Links inside the mount root are refused.");
        }
    }
}
