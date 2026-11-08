namespace Bazaar.Server.Souls.Bundles;

/// <summary>Validates bundle-relative paths. A valid path is relative, uses forward slashes and has no empty, "." or ".." segments.</summary>
public static class BundlePaths
{
    private const int _maxLength = 512;

    /// <summary>Returns the path unchanged when it is valid; throws <see cref="ArgumentException"/> otherwise.</summary>
    public static string Validate(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (path.Length == 0 || path.Length > _maxLength)
            throw new ArgumentException($"Bundle path must be 1 to {_maxLength} characters.", nameof(path));
        if (path[0] == '/' || path.Contains(':', StringComparison.Ordinal))
            throw new ArgumentException($"Bundle path '{path}' must be relative (no leading slash, no drive or scheme).", nameof(path));
        if (path.Contains('\\', StringComparison.Ordinal))
            throw new ArgumentException($"Bundle path '{path}' must use forward slashes only.", nameof(path));
        foreach (var c in path)
        {
            if (char.IsControl(c))
                throw new ArgumentException("Bundle path contains a control character.", nameof(path));
        }

        foreach (var segment in path.Split('/'))
        {
            if (segment.Length == 0)
                throw new ArgumentException($"Bundle path '{path}' has an empty segment (leading, trailing or doubled slash).", nameof(path));
            if (segment is "." or "..")
                throw new ArgumentException($"Bundle path '{path}' contains a '{segment}' segment.", nameof(path));
        }

        return path;
    }
}
