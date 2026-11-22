using System.Globalization;
using System.Text.RegularExpressions;

namespace Bazaar.Server.Godot;

/// <summary>
/// A parsed <c>godot --version</c> line, for example <c>4.7.2.stable.official.0123abcd</c> or
/// <c>4.7.2.stable.mono.official.0123abcd</c>. Assumed shape (from memory, not verified against a 4.7.2 binary):
/// <c>major.minor[.patch].status[.mono][.build][.hash]</c>. The .NET build carries the <c>mono</c> token.
/// </summary>
internal sealed partial record GodotVersion(int Major, int Minor, int Patch, bool HasPatch, string Status, bool IsMono, bool IsTemplate)
{
    /// <summary>The version without build or hash, for example <c>4.7.2.stable</c>. The patch is omitted when the engine omits it (<c>4.7.stable</c>).</summary>
    public string Display => (HasPatch ? $"{Major}.{Minor}.{Patch}" : $"{Major}.{Minor}") + "." + Status;

    /// <summary>The export-templates folder name for this version (<c>4.7.2.stable</c>, plus <c>.mono</c> for the .NET build).</summary>
    public string TemplatesFolder => IsMono ? Display + ".mono" : Display;

    /// <summary>Parses the first non-blank line. Returns null when it does not look like a Godot version.</summary>
    public static GodotVersion? Parse(string output)
    {
        ArgumentNullException.ThrowIfNull(output);
        foreach (string raw in output.Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            Match match = VersionLine().Match(line);
            if (!match.Success)
            {
                return null;
            }

            string rest = match.Groups["rest"].Value;
            string[] tokens = rest.Split('.', StringSplitOptions.RemoveEmptyEntries);
            return new GodotVersion(
                int.Parse(match.Groups["major"].Value, CultureInfo.InvariantCulture),
                int.Parse(match.Groups["minor"].Value, CultureInfo.InvariantCulture),
                match.Groups["patch"].Success ? int.Parse(match.Groups["patch"].Value, CultureInfo.InvariantCulture) : 0,
                match.Groups["patch"].Success,
                match.Groups["status"].Value,
                tokens.Contains("mono", StringComparer.Ordinal),
                tokens.Any(t => t.Contains("template", StringComparison.OrdinalIgnoreCase)));
        }

        return null;
    }

    /// <summary>Parses a pin such as <c>4.7.2</c> or <c>4.7</c> into major, minor and patch (patch 0 when absent). Null when malformed.</summary>
    public static (int Major, int Minor, int Patch)? ParsePin(string pin)
    {
        Match match = PinLine().Match(pin ?? string.Empty);
        if (!match.Success)
        {
            return null;
        }

        return (
            int.Parse(match.Groups["major"].Value, CultureInfo.InvariantCulture),
            int.Parse(match.Groups["minor"].Value, CultureInfo.InvariantCulture),
            match.Groups["patch"].Success ? int.Parse(match.Groups["patch"].Value, CultureInfo.InvariantCulture) : 0);
    }

    [GeneratedRegex(@"^(?<major>\d{1,3})\.(?<minor>\d{1,3})(?:\.(?<patch>\d{1,3}))?\.(?<status>[a-z]+\d*)(?<rest>(?:\.[A-Za-z0-9_]+)*)$")]
    private static partial Regex VersionLine();

    [GeneratedRegex(@"^(?<major>\d{1,3})\.(?<minor>\d{1,3})(?:\.(?<patch>\d{1,3}))?$")]
    private static partial Regex PinLine();
}
