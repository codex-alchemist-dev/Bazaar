using System.Text;

namespace Bazaar.Server.RepoMap;

/// <summary>
/// Path and text rules shared by the search tool and the repo map. Paths from tool output are untrusted and are checked here before use.
/// </summary>
internal static class RepoMapRules
{
    private const int _maxPathLength = 4096;

    /// <summary>Strips a leading "./" that ripgrep adds to paths. Maps an empty result to ".".</summary>
    public static string Normalize(string path)
    {
        var trimmed = path.StartsWith("./", StringComparison.Ordinal) ? path[2..] : path;
        return trimmed.Length == 0 ? "." : trimmed;
    }

    /// <summary>True for "." or a safe worktree-relative path.</summary>
    public static bool IsSafeRoot(string root) => root == "." || IsSafeRelative(Normalize(root));

    /// <summary>
    /// True for a non-empty relative path with forward slashes, no empty, "." or ".." segment, no drive colon, no backslash and no control characters.
    /// </summary>
    public static bool IsSafeRelative(string path)
    {
        if (path.Length == 0 || path.Length > _maxPathLength || path[0] == '/')
        {
            return false;
        }

        foreach (var c in path)
        {
            if (char.IsControl(c) || c == '\\' || c == ':')
            {
                return false;
            }
        }

        foreach (var segment in path.Split('/'))
        {
            if (segment.Length == 0 || segment == "." || segment == "..")
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>True when any segment is bin, obj or .git. These are never mapped or searched.</summary>
    public static bool IsExcluded(string path)
    {
        foreach (var segment in path.Split('/'))
        {
            if (segment is "bin" or "obj" or ".git")
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Collapses all whitespace and control characters to single spaces, then caps the length with "...".</summary>
    public static string SingleLine(string value, int maxLength)
    {
        var cleaned = new string(value.Select(c => char.IsControl(c) ? ' ' : c).ToArray());
        var joined = string.Join(' ', cleaned.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return joined.Length <= maxLength ? joined : joined[..(maxLength - 3)] + "...";
    }

    /// <summary>Replaces control characters, except tab, with spaces, trims the end and caps the length. Keeps indentation.</summary>
    public static string Printable(string value, int maxLength)
    {
        var sb = new StringBuilder(Math.Min(value.Length, maxLength));
        foreach (var c in value)
        {
            if (sb.Length >= maxLength)
            {
                break;
            }

            sb.Append(char.IsControl(c) && c != '\t' ? ' ' : c);
        }

        return sb.ToString().TrimEnd();
    }
}
