namespace Bazaar.Server.Identity.Mount;

/// <summary>
/// An in-memory <see cref="IMountFileSystem"/> for tests and for the writer's contract checks. It holds no real files.
/// </summary>
public sealed class InMemoryMountFileSystem : IMountFileSystem
{
    private readonly Dictionary<string, byte[]> _files = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    /// <inheritdoc />
    public void WriteFile(string relativePath, ReadOnlyMemory<byte> content)
    {
        var key = Normalize(relativePath, allowEmpty: false);
        lock (_gate)
        {
            _files[key] = content.ToArray();
        }
    }

    /// <inheritdoc />
    public void DeleteFile(string relativePath)
    {
        var key = Normalize(relativePath, allowEmpty: false);
        lock (_gate)
        {
            _files.Remove(key);
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<string> ListFiles(string directory)
    {
        var prefix = PrefixOf(directory);
        lock (_gate)
        {
            return _files.Keys
                .Where(k => k.StartsWith(prefix, StringComparison.Ordinal))
                .Order(StringComparer.Ordinal)
                .ToList();
        }
    }

    /// <inheritdoc />
    public bool Exists(string relativePath)
    {
        var key = Normalize(relativePath, allowEmpty: false);
        lock (_gate)
        {
            return _files.ContainsKey(key);
        }
    }

    /// <inheritdoc />
    public void Detach(string directory)
    {
        var prefix = PrefixOf(Normalize(directory, allowEmpty: false));
        lock (_gate)
        {
            foreach (var key in _files.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToList())
            {
                _files.Remove(key);
            }
        }
    }

    /// <summary>Test helper: reads a stored file's bytes.</summary>
    public bool TryRead(string relativePath, out byte[] content)
    {
        var key = Normalize(relativePath, allowEmpty: false);
        lock (_gate)
        {
            if (_files.TryGetValue(key, out var stored))
            {
                content = stored.ToArray();
                return true;
            }
        }

        content = [];
        return false;
    }

    private static string PrefixOf(string directory)
    {
        if (directory.Length == 0)
        {
            return string.Empty;
        }

        return Normalize(directory, allowEmpty: false) + "/";
    }

    private static string Normalize(string relativePath, bool allowEmpty)
    {
        if (string.IsNullOrEmpty(relativePath))
        {
            if (allowEmpty)
            {
                return string.Empty;
            }

            throw new ArgumentException("A relative path is required.", nameof(relativePath));
        }

        if (Path.IsPathRooted(relativePath) || relativePath.Contains('\\', StringComparison.Ordinal))
        {
            throw new ArgumentException("Mount paths must be relative and use '/' separators.", nameof(relativePath));
        }

        foreach (var segment in relativePath.Split('/'))
        {
            if (segment.Length == 0 || segment == "." || segment == "..")
            {
                throw new ArgumentException("Mount paths may not contain empty or traversal segments.", nameof(relativePath));
            }
        }

        return relativePath;
    }
}
