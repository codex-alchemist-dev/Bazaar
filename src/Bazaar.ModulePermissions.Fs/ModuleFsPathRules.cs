namespace Bazaar.ModulePermissions.Fs;

/// <summary>
/// The file-name rules a module path must pass on top of <see cref="ModuleTargetRules"/>. They follow the same rules as the
/// Rojo path guard in the engines module (control characters, colon, trailing dot or space, Windows device names), and are
/// applied on every OS so a module behaves the same on Windows and Linux.
/// </summary>
internal static class ModuleFsPathRules
{
    private const string _invalidChars = "<>:\"/\\|?*";
    private const int _maxSegmentLength = 255;

    /// <summary>True when <paramref name="segment"/> is a file name every supported OS reads as itself.</summary>
    public static bool IsValidSegment(string? segment)
    {
        if (string.IsNullOrEmpty(segment) || segment.Length > _maxSegmentLength)
        {
            return false;
        }

        // Windows drops a trailing dot or space silently, so the name checked would not be the name opened.
        if (segment is "." or ".." || segment[^1] is '.' or ' ')
        {
            return false;
        }

        foreach (var c in segment)
        {
            if (char.IsControl(c) || _invalidChars.Contains(c, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return !IsReservedDeviceName(segment);
    }

    private static bool IsReservedDeviceName(string segment)
    {
        var dot = segment.IndexOf('.', StringComparison.Ordinal);
        var stem = (dot < 0 ? segment : segment[..dot]).TrimEnd();
        if (stem.Equals("CON", StringComparison.OrdinalIgnoreCase)
            || stem.Equals("PRN", StringComparison.OrdinalIgnoreCase)
            || stem.Equals("AUX", StringComparison.OrdinalIgnoreCase)
            || stem.Equals("NUL", StringComparison.OrdinalIgnoreCase)
            || stem.Equals("CONIN$", StringComparison.OrdinalIgnoreCase)
            || stem.Equals("CONOUT$", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // COM1 to COM9 and LPT1 to LPT9, and the superscript digits Windows also reads as device numbers.
        return stem.Length == 4
            && (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase) || stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase))
            && (stem[3] is >= '0' and <= '9' or '¹' or '²' or '³');
    }
}
