namespace Bazaar.Server.Modules.Trust;

/// <summary>
/// SemVer 2.0 precedence for module versions (https://semver.org section 11). Build metadata is ignored, as the spec says.
/// The Verified tag decides revocation by line order, so a line must be ordered by this comparison, not by the caller's list.
/// </summary>
public static class ModuleVersionOrder
{
    /// <summary>Parses <paramref name="version"/> into a comparable form, or throws when it is not SemVer.</summary>
    public static ParsedModuleVersion Parse(string version)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(version);

        var rest = version;
        var plus = rest.IndexOf('+', StringComparison.Ordinal);
        if (plus >= 0)
        {
            rest = rest[..plus];
        }

        var dash = rest.IndexOf('-', StringComparison.Ordinal);
        var core = dash >= 0 ? rest[..dash] : rest;
        var pre = dash >= 0 ? rest[(dash + 1)..] : null;

        var parts = core.Split('.');
        if (parts.Length != 3 || !parts.All(IsNumericIdentifier))
        {
            throw new ArgumentException($"'{version}' is not a SemVer version (MAJOR.MINOR.PATCH).", nameof(version));
        }

        if (pre is not null && !pre.Split('.').All(id => id.Length > 0 && id.All(IsIdentifierChar)))
        {
            throw new ArgumentException($"'{version}' has an invalid pre-release identifier.", nameof(version));
        }

        return new ParsedModuleVersion(ParseNumber(parts[0], version), ParseNumber(parts[1], version), ParseNumber(parts[2], version), pre);
    }

    private static long ParseNumber(string part, string version) =>
        long.TryParse(part, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var number)
            ? number
            : throw new ArgumentException($"'{version}' has a version number outside the supported range.", nameof(version));

    /// <summary>Compares two versions by SemVer precedence. Negative when <paramref name="left"/> is older.</summary>
    public static int Compare(string left, string right) => Parse(left).ComparePrecedence(Parse(right));

    private static bool IsNumericIdentifier(string part) =>
        part.Length > 0 && part.All(char.IsAsciiDigit) && (part.Length == 1 || part[0] != '0');

    private static bool IsIdentifierChar(char c) => char.IsAsciiLetterOrDigit(c) || c == '-';
}

/// <summary>A parsed SemVer version: the three numbers and the optional pre-release text.</summary>
public sealed record ParsedModuleVersion(long Major, long Minor, long Patch, string? PreRelease)
{
    public int ComparePrecedence(ParsedModuleVersion other)
    {
        ArgumentNullException.ThrowIfNull(other);

        var core = Major.CompareTo(other.Major);
        if (core != 0) return core;
        core = Minor.CompareTo(other.Minor);
        if (core != 0) return core;
        core = Patch.CompareTo(other.Patch);
        if (core != 0) return core;

        // A version with no pre-release is greater than the same version with one.
        if (PreRelease is null && other.PreRelease is null) return 0;
        if (PreRelease is null) return 1;
        if (other.PreRelease is null) return -1;

        var left = PreRelease.Split('.');
        var right = other.PreRelease.Split('.');
        for (var i = 0; i < Math.Min(left.Length, right.Length); i++)
        {
            var result = ComparePreReleaseIdentifier(left[i], right[i]);
            if (result != 0) return result;
        }

        // A longer list of identifiers with an equal prefix is greater.
        return left.Length.CompareTo(right.Length);
    }

    // Numeric identifiers compare as numbers and rank below alphanumeric ones; alphanumeric ones compare in ASCII order.
    private static int ComparePreReleaseIdentifier(string left, string right)
    {
        var leftNumeric = left.All(char.IsAsciiDigit);
        var rightNumeric = right.All(char.IsAsciiDigit);
        if (leftNumeric && rightNumeric)
        {
            // Compare by length, then lexically, so huge numbers never overflow.
            var byLength = left.Length.CompareTo(right.Length);
            return byLength != 0 ? byLength : string.CompareOrdinal(left, right);
        }

        if (leftNumeric) return -1;
        if (rightNumeric) return 1;
        return string.CompareOrdinal(left, right);
    }
}
