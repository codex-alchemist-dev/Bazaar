using System.Globalization;
using System.Reflection;

namespace Bazaar.Contracts;

/// <summary>
/// The product version and its major-version codename (docs/VERSIONING.md).
/// The number comes from <c>VersionPrefix</c> in Directory.Build.props; the SDK appends the
/// commit hash as SemVer build metadata (<c>1.2.3+a1b2c3d</c>).
/// </summary>
public static class ProductVersion
{
    // Keyed by MAJOR only. A living list: add a name when that major actually ships, never
    // remove one (an LTSC install on an old major still shows its own codename).
    private static readonly Dictionary<int, string> _codenames = new()
    {
        [0] = "Beta",
        [1] = "Zuhal",
    };

    /// <summary>The running build's informational version, for example <c>0.1.0+a1b2c3d</c>.</summary>
    public static string Informational { get; } =
        typeof(ProductVersion).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? "0.0.0";

    /// <summary>The codename of the running build's major version, or <c>null</c> if it has none yet.</summary>
    public static string? CurrentCodename => Codename(Major(Informational));

    /// <summary>Returns the codename for a major version, or <c>null</c> if that major has none yet.</summary>
    public static string? Codename(int major) => _codenames.GetValueOrDefault(major);

    /// <summary>Returns the MAJOR part of a SemVer string such as <c>v1.2.3-beta.1+a1b2c3d</c>.</summary>
    public static int Major(string version)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        var trimmed = version.StartsWith('v') ? version[1..] : version;
        var end = trimmed.IndexOf('.', StringComparison.Ordinal);
        return int.Parse(end < 0 ? trimmed : trimmed[..end], NumberStyles.None, CultureInfo.InvariantCulture);
    }
}
