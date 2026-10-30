namespace Bazaar.Server.Backup.Manifest;

/// <summary>
/// Normalises bundle-relative paths so one logical path has one spelling. Backslashes become forward slashes.
/// Absolute paths, drive letters, empty segments, "." and ".." segments and control characters are rejected.
/// </summary>
public static class BundlePath
{
    public static string Normalize(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (path.Length == 0)
        {
            throw new ArgumentException("Bundle path must not be empty.", nameof(path));
        }

        foreach (var c in path)
        {
            if (c < ' ' || c == '\u007f')
            {
                throw new ArgumentException("Bundle path must not contain control characters.", nameof(path));
            }
        }

        var unified = path.Replace('\\', '/');
        if (unified.StartsWith('/') || (unified.Length >= 2 && unified[1] == ':' && char.IsAsciiLetter(unified[0])))
        {
            throw new ArgumentException($"Bundle path must be relative: '{path}'.", nameof(path));
        }

        var segments = unified.Split('/');
        foreach (var segment in segments)
        {
            if (segment.Length == 0 || segment == "." || segment == "..")
            {
                throw new ArgumentException($"Bundle path has an empty, '.' or '..' segment: '{path}'.", nameof(path));
            }
        }

        return string.Join('/', segments);
    }
}
