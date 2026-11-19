using System.Globalization;

namespace Bazaar.Updates;

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

        if (preRelease.Length > 0 && !IsValidPreRelease(preRelease))
        {
            return false;
        }

        var parts = value.Split('.');
        if (parts.Length != 3
            || parts.Any(part => part.Length > 1 && part[0] == '0')
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

        // SemVer 2.0 section 11: compare dot-separated identifiers left to right. Numeric identifiers compare as numbers
        // (so rc.2 is older than rc.10) and rank below alphanumeric ones; a longer list with an equal prefix is newer.
        var left = PreRelease.Split('.');
        var right = other.PreRelease.Split('.');
        for (var i = 0; i < Math.Min(left.Length, right.Length); i++)
        {
            var result = ComparePreReleaseIdentifier(left[i], right[i]);
            if (result != 0)
            {
                return result;
            }
        }

        return left.Length.CompareTo(right.Length);
    }

    private static bool IsValidPreRelease(string preRelease) =>
        preRelease.Split('.').All(id =>
            id.Length > 0
            && id.All(c => char.IsAsciiLetterOrDigit(c) || c == '-')
            && !(id.Length > 1 && id[0] == '0' && id.All(char.IsAsciiDigit)));

    private static int ComparePreReleaseIdentifier(string left, string right)
    {
        var leftNumeric = left.All(char.IsAsciiDigit);
        var rightNumeric = right.All(char.IsAsciiDigit);
        if (leftNumeric && rightNumeric)
        {
            // By length, then lexically, so a huge number never overflows.
            var byLength = left.Length.CompareTo(right.Length);
            return byLength != 0 ? Math.Sign(byLength) : Math.Sign(string.CompareOrdinal(left, right));
        }

        if (leftNumeric)
        {
            return -1;
        }

        if (rightNumeric)
        {
            return 1;
        }

        return Math.Sign(string.CompareOrdinal(left, right));
    }

    public override string ToString() =>
        PreRelease.Length == 0 ? $"{Major}.{Minor}.{Patch}" : $"{Major}.{Minor}.{Patch}-{PreRelease}";

    public static bool operator <(UpdateVersion left, UpdateVersion right) => left.CompareTo(right) < 0;

    public static bool operator >(UpdateVersion left, UpdateVersion right) => left.CompareTo(right) > 0;

    public static bool operator <=(UpdateVersion left, UpdateVersion right) => left.CompareTo(right) <= 0;

    public static bool operator >=(UpdateVersion left, UpdateVersion right) => left.CompareTo(right) >= 0;
}
