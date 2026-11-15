using System.Globalization;
using System.Text.RegularExpressions;

namespace Bazaar.Components.ManagedPostgres.Bundle;

/// <summary>Version reported by <c>postgres --version</c>.</summary>
/// <param name="Major">PostgreSQL major, for example 18 or 19.</param>
/// <param name="Minor">Minor release; 0 for pre-releases such as 19beta1.</param>
/// <param name="PreRelease">Pre-release tag such as "beta1" or "rc1"; null for release builds.</param>
public sealed record ServerVersion(int Major, int Minor, string? PreRelease);

/// <summary>Raised when <c>postgres --version</c> output is not in the expected form.</summary>
public sealed class ServerVersionParseException : Exception
{
    /// <param name="output">The text that could not be parsed.</param>
    public ServerVersionParseException(string output)
        : base($"Unrecognised postgres --version output: '{output}'. Expected 'postgres (PostgreSQL) <major>[.<minor>]'.")
    {
        Output = output;
    }

    /// <summary>The text that could not be parsed.</summary>
    public string Output { get; }
}

/// <summary>Parses <c>postgres --version</c> output. Pure: running the binary is the caller's job.</summary>
public static partial class ServerVersionProbe
{
    [GeneratedRegex(@"^\s*postgres \(PostgreSQL\) (\d{1,3})(?:\.(\d{1,3}))?((?:alpha|beta|rc)\d{1,3})?\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex VersionLine();

    /// <summary>Parses output such as "postgres (PostgreSQL) 18.6" or "postgres (PostgreSQL) 19beta1".</summary>
    /// <exception cref="ServerVersionParseException">The text is not a recognised version line.</exception>
    public static ServerVersion Parse(string output)
    {
        ArgumentNullException.ThrowIfNull(output);
        var match = VersionLine().Match(output.TrimEnd('\r', '\n'));
        if (!match.Success)
        {
            throw new ServerVersionParseException(output);
        }

        var major = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        var minor = match.Groups[2].Success ? int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture) : 0;
        var preRelease = match.Groups[3].Success ? match.Groups[3].Value : null;
        return new ServerVersion(major, minor, preRelease);
    }

    /// <summary>True when the major is one Bazaar ships a bundle for.</summary>
    public static bool IsSupportedMajor(int major) => BundleLayout.SupportedMajors.Contains(major);
}
