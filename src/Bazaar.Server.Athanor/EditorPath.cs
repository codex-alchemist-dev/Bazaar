using System.Text;

namespace Bazaar.Server.Athanor;

/// <summary>
/// Path rules for editor requests (doc 57 section 5 and 6). The path must stay inside the workspace root: no drive or stream
/// separator (<c>:</c>), no absolute path, no <c>..</c>. Backslashes are read as separators. The result is relative with '/' separators.
/// Symlink resolution is the store's job; this check is lexical and runs first.
/// </summary>
public static class EditorPath
{
    private const int _maxLength = 1024;

    /// <summary>Normalises <paramref name="raw"/>; returns false with a reason when it is not acceptable.</summary>
    public static bool TryNormalize(string? raw, out string normalized, out string error)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(raw))
        {
            error = "A path is required.";
            return false;
        }

        if (raw.Length > _maxLength)
        {
            error = "The path is too long.";
            return false;
        }

        foreach (var c in raw)
        {
            if (c == ':')
            {
                error = "A path may not contain ':'.";
                return false;
            }

            if (char.IsControl(c))
            {
                error = "A path may not contain control characters.";
                return false;
            }
        }

        var unified = raw.Replace('\\', '/');
        if (unified[0] == '/')
        {
            error = "A path must be relative to the workspace.";
            return false;
        }

        if (unified[^1] == '/')
        {
            error = "A path must name a file.";
            return false;
        }

        var parts = new List<string>();
        foreach (var segment in unified.Split('/'))
        {
            if (segment.Length == 0 || segment == ".")
            {
                continue;
            }

            if (segment == "..")
            {
                error = "A path may not leave the workspace.";
                return false;
            }

            // Windows drops trailing dots and spaces, so "a." would alias "a".
            if (segment[^1] is '.' or ' ')
            {
                error = "A path segment may not end with '.' or a space.";
                return false;
            }

            parts.Add(segment);
        }

        if (parts.Count == 0)
        {
            error = "A path must name a file.";
            return false;
        }

        var result = string.Join('/', parts);
        if (Encoding.UTF8.GetByteCount(result) > _maxLength)
        {
            error = "The path is too long.";
            return false;
        }

        normalized = result;
        error = string.Empty;
        return true;
    }
}
