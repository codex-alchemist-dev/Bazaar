using System.Globalization;

namespace Bazaar.Client.Core.Updates;

/// <summary>
/// A SemVer version as Bazaar writes it: <c>0.MM.PP</c>, optional <c>-prerelease</c>, optional <c>+commit</c>.
/// Build metadata (the commit) never affects precedence, so <c>0.1.0+a1b2c3d</c> equals <c>0.1.0</c>.
/// </summary>
public readonly record struct UpdateVersion : IComparable<UpdateVersion>
{
    private UpdateVersion(int major, int minor, int patch, string preRelease)
    {
        Major = major;
        Minor = minor;
        Patch = patch;
        PreRelease = preRelease;
    }

    public int Major { get; }

    public int Minor { get; }

    public int Patch { get; }

    /// <summary>The pre-release tag, or empty for a release.</summary>
    public string PreRelease { get; }

    /// <summary>Parses a version. Returns false for anything that is not a valid SemVer core.</summary>
    public static bool TryParse(string? text, out UpdateVersion version)
    {
        version = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var value = text.Trim();
        var plus = value.IndexOf('+', StringComparison.Ordinal);
        if (plus >= 0)
        {
            value = value[..plus];
        }

        var preRelease = string.Empty;
        var dash = value.IndexOf('-', StringComparison.Ordinal);
        if (dash >= 0)
        {
            preRelease = value[(dash + 1)..];
            value = value[..dash];
            if (preRelease.Length == 0)
            {
                return false;
            }
        }

        var parts = value.Split('.');
        if (parts.Length != 3
            || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var major)
            || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var minor)
            || !int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var patch))
        {
            return false;
        }

        version = new UpdateVersion(major, minor, patch, preRelease);
        return true;
    }

    /// <summary>Parses a version and throws when it is not valid.</summary>
    public static UpdateVersion Parse(string text) =>
        TryParse(text, out var version) ? version : throw new FormatException($"Not a Bazaar version: '{text}'.");

    /// <summary>Orders versions by SemVer precedence. A release outranks its own pre-release.</summary>
    public int CompareTo(UpdateVersion other)
    {
        var core = Major.CompareTo(other.Major);
        if (core != 0)
        {
            return core;
        }

        core = Minor.CompareTo(other.Minor);
        if (core != 0)
        {
            return core;
        }

        core = Patch.CompareTo(other.Patch);
        if (core != 0)
        {
            return core;
        }

        if (PreRelease.Length == 0 && other.PreRelease.Length == 0)
        {
            return 0;
        }

        if (PreRelease.Length == 0)
        {
            return 1;
        }

        if (other.PreRelease.Length == 0)
        {
            return -1;
        }

        return Math.Sign(string.CompareOrdinal(PreRelease, other.PreRelease));
    }

    public override string ToString() =>
        PreRelease.Length == 0 ? $"{Major}.{Minor}.{Patch}" : $"{Major}.{Minor}.{Patch}-{PreRelease}";

    public static bool operator <(UpdateVersion left, UpdateVersion right) => left.CompareTo(right) < 0;

    public static bool operator >(UpdateVersion left, UpdateVersion right) => left.CompareTo(right) > 0;

    public static bool operator <=(UpdateVersion left, UpdateVersion right) => left.CompareTo(right) <= 0;

    public static bool operator >=(UpdateVersion left, UpdateVersion right) => left.CompareTo(right) >= 0;
}
