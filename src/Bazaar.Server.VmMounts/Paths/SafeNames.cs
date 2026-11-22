using System.Text.RegularExpressions;

namespace Bazaar.Server.VmMounts.Paths;

/// <summary>
/// Name rules shared by the host path guard and the profile transfer. They are strict on purpose and identical on every OS, so a
/// name that is safe on a Linux host is also safe when the volume is later read on Windows: no trailing dot or space (Windows
/// drops them, so two names alias), no reserved device names, no 8.3 short names, no stream or drive separators.
/// </summary>
internal static partial class SafeNames
{
    private static readonly HashSet<string> _reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM0", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT0", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    private const string _forbiddenChars = "<>:\"|?*\\/";

    [GeneratedRegex("~[0-9]", RegexOptions.CultureInvariant)]
    private static partial Regex ShortNameMarker();

    /// <summary>Whether one path segment is acceptable. A segment never holds a separator.</summary>
    public static bool IsSafeSegment(string segment)
    {
        if (string.IsNullOrEmpty(segment) || segment.Length > 255 || segment is "." or "..")
            return false;
        if (segment[^1] is '.' or ' ')
            return false;
        foreach (var c in segment)
        {
            if (c < 0x20 || c == 0x7F || _forbiddenChars.Contains(c, StringComparison.Ordinal))
                return false;
        }

        var dot = segment.IndexOf('.', StringComparison.Ordinal);
        var stem = dot < 0 ? segment : segment[..dot];
        if (_reserved.Contains(stem.TrimEnd(' ')))
            return false;
        return !ShortNameMarker().IsMatch(segment);
    }

    /// <summary>Whether a relative path with <c>/</c> separators is acceptable: at most 32 deep, every segment safe.</summary>
    public static bool IsSafeRelativePath(string path)
    {
        if (string.IsNullOrEmpty(path) || path.Length > 1024 || path[0] == '/')
            return false;
        var segments = path.Split('/');
        return segments.Length <= 32 && segments.All(IsSafeSegment);
    }

    /// <summary>Whether <paramref name="path"/> is <paramref name="root"/> or below it, by whole segments.</summary>
    public static bool IsUnderOrEqual(string root, string path, bool caseInsensitive)
    {
        var cmp = caseInsensitive ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var r = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var p = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (p.Equals(r, cmp))
            return true;
        return p.Length > r.Length
            && p.StartsWith(r, cmp)
            && (p[r.Length] == Path.DirectorySeparatorChar || p[r.Length] == Path.AltDirectorySeparatorChar);
    }

    /// <summary>The path below <paramref name="root"/> as segments, or null when it is not under it.</summary>
    public static string[]? SegmentsBelow(string root, string path, bool caseInsensitive)
    {
        if (!IsUnderOrEqual(root, path, caseInsensitive))
            return null;
        var r = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var rest = path[r.Length..].Trim(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return rest.Length == 0 ? [] : rest.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }
}
