namespace Bazaar.Contracts.Vms;

/// <summary>
/// Rules for the root-relative paths of the workspace file operations (DeleteFile, ListFiles, ResolveRealPath). The guest
/// applies them to what the host asks for, and the host applies the structural ones to what the guest answers.
/// </summary>
public static class GuestPathRules
{
    /// <summary>Longest path, in characters, the host sends and the guest echoes back in an answer.</summary>
    public const int MaxPathChars = 4096;

    private static readonly string[] _reservedDeviceNames =
    [
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    ];

    /// <summary>
    /// True when <paramref name="path"/> is a safe root-relative path: not rooted, no drive or UNC prefix, no <c>..</c>
    /// segment, no NUL, no <c>:</c> anywhere, and no more than <see cref="MaxPathChars"/> characters. An empty path
    /// (or <c>.</c>) names the root itself and is accepted only with <paramref name="allowRoot"/>. With
    /// <paramref name="windows"/>, segments that end in a dot or a space or name a reserved device (CON, NUL, COM1 and so on,
    /// with or without an extension) are refused too.
    /// </summary>
    public static bool IsSafeRelative(string? path, bool allowRoot, bool windows = false)
    {
        if (path is null || path.Length > MaxPathChars || path.Contains('\0') || path.Contains(':'))
        {
            return false;
        }

        if (path.StartsWith('/') || path.StartsWith('\\'))
        {
            return false;
        }

        var segments = path.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);
        foreach (var segment in segments)
        {
            if (segment == ".." || (windows && IsWindowsUnsafe(segment)))
            {
                return false;
            }
        }

        return allowRoot || segments.Any(segment => segment != ".");
    }

    /// <summary>True when <paramref name="name"/> is a plain directory entry name: non-empty, no separator, NUL or colon, not <c>.</c> or <c>..</c>.</summary>
    public static bool IsSafeEntryName(string? name) =>
        !string.IsNullOrEmpty(name) && name is not ("." or "..") && name.IndexOfAny(['/', '\\', '\0', ':']) < 0;

    private static bool IsWindowsUnsafe(string segment)
    {
        // A lone "." means the current directory and is harmless; "a." and "a " are not.
        if (segment != "." && (segment.EndsWith('.') || segment.EndsWith(' ')))
        {
            return true;
        }

        var dot = segment.IndexOf('.');
        var stem = (dot < 0 ? segment : segment[..dot]).TrimEnd(' ');
        return _reservedDeviceNames.Contains(stem, StringComparer.OrdinalIgnoreCase);
    }
}
