using System.Text.RegularExpressions;

namespace Bazaar.Server.CodingWorkspace;

/// <summary>
/// Rules for one path segment built from an id. No separators, no dot-names, no trailing dot or space, no Windows device
/// names, so the segment names exactly one directory on every supported host.
/// </summary>
internal static partial class SafeSegment
{
    private static readonly HashSet<string> _deviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>True when the id is safe to use as a single directory name.</summary>
    public static bool IsValid(string? id)
    {
        if (id is null || !Pattern().IsMatch(id) || id.EndsWith('.'))
        {
            return false;
        }

        var stem = id.Split('.')[0];
        return !_deviceNames.Contains(stem);
    }

    [GeneratedRegex(@"^[A-Za-z0-9_][A-Za-z0-9._-]{0,99}$", RegexOptions.CultureInvariant)]
    private static partial Regex Pattern();
}
