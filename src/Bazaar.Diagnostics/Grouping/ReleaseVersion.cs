using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;

namespace Bazaar.Diagnostics.Grouping;

/// <summary>
/// A release version for SemVer precedence (SemVer 2.0.0, clause 11). The build metadata (the <c>+commit</c>
/// suffix, docs/VERSIONING.md) is kept in <see cref="Text"/> but ignored for precedence, so <c>0.2.0+a1b2c3d</c>
/// has the same precedence as <c>0.2.0</c>. An optional leading <c>v</c> is accepted for release tags.
/// </summary>
public sealed partial class ReleaseVersion : IComparable<ReleaseVersion>
{
    private const string _identifierPattern = @"[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*";

    private ReleaseVersion(string text, int major, int minor, int patch, string? preRelease)
    {
        Text = text;
        Major = major;
        Minor = minor;
        Patch = patch;
        PreRelease = preRelease;
    }

    /// <summary>The version as it was written, including any build metadata.</summary>
    public string Text { get; }

    /// <summary>The major number.</summary>
    public int Major { get; }

    /// <summary>The minor number.</summary>
    public int Minor { get; }

    /// <summary>The patch number.</summary>
    public int Patch { get; }

    /// <summary>The pre-release identifiers, or null for a release.</summary>
    public string? PreRelease { get; }

    /// <summary>Parses a version, throwing <see cref="FormatException"/> when it is not SemVer.</summary>
    public static ReleaseVersion Parse(string text) =>
        TryParse(text, out var version) ? version : throw new FormatException($"Not a SemVer version: '{text}'.");

    /// <summary>Parses a version without throwing.</summary>
    public static bool TryParse(string? text, [NotNullWhen(true)] out ReleaseVersion? version)
    {
        version = null;
        if (text is null)
        {
            return false;
        }

        var match = _versionRegex().Match(text);
        if (!match.Success)
        {
            return false;
        }

        if (!int.TryParse(match.Groups["major"].ValueSpan, out var major) ||
            !int.TryParse(match.Groups["minor"].ValueSpan, out var minor) ||
            !int.TryParse(match.Groups["patch"].ValueSpan, out var patch))
        {
            return false;
        }

        var preRelease = match.Groups["pre"].Success ? match.Groups["pre"].Value : null;
        version = new ReleaseVersion(text, major, minor, patch, preRelease);
        return true;
    }

    /// <inheritdoc />
    public int CompareTo(ReleaseVersion? other)
    {
        if (other is null)
        {
            return 1;
        }

        var core = Major.CompareTo(other.Major);
        if (core == 0)
        {
            core = Minor.CompareTo(other.Minor);
        }

        if (core == 0)
        {
            core = Patch.CompareTo(other.Patch);
        }

        return core != 0 ? core : ComparePreRelease(PreRelease, other.PreRelease);
    }

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is ReleaseVersion other && CompareTo(other) == 0;

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(Major, Minor, Patch, PreRelease);

    /// <summary>Compares two versions by precedence.</summary>
    public static bool operator ==(ReleaseVersion? left, ReleaseVersion? right) =>
        left is null ? right is null : left.CompareTo(right) == 0;

    /// <summary>Compares two versions by precedence.</summary>
    public static bool operator !=(ReleaseVersion? left, ReleaseVersion? right) => !(left == right);

    /// <summary>Compares two versions by precedence.</summary>
    public static bool operator <(ReleaseVersion? left, ReleaseVersion? right) =>
        left is null ? right is not null : left.CompareTo(right) < 0;

    /// <summary>Compares two versions by precedence.</summary>
    public static bool operator <=(ReleaseVersion? left, ReleaseVersion? right) =>
        left is null || left.CompareTo(right) <= 0;

    /// <summary>Compares two versions by precedence.</summary>
    public static bool operator >(ReleaseVersion? left, ReleaseVersion? right) =>
        left is not null && left.CompareTo(right) > 0;

    /// <summary>Compares two versions by precedence.</summary>
    public static bool operator >=(ReleaseVersion? left, ReleaseVersion? right) =>
        left is null ? right is null : left.CompareTo(right) >= 0;

    /// <inheritdoc />
    public override string ToString() => Text;

    private static int ComparePreRelease(string? left, string? right)
    {
        if (left is null || right is null)
        {
            // A release has higher precedence than any pre-release of the same core version.
            return left is null ? (right is null ? 0 : 1) : -1;
        }

        var leftIds = left.Split('.');
        var rightIds = right.Split('.');
        for (var i = 0; i < Math.Min(leftIds.Length, rightIds.Length); i++)
        {
            var result = CompareIdentifier(leftIds[i], rightIds[i]);
            if (result != 0)
            {
                return result;
            }
        }

        return leftIds.Length.CompareTo(rightIds.Length);
    }

    private static int CompareIdentifier(string left, string right)
    {
        var leftNumeric = IsNumeric(left);
        var rightNumeric = IsNumeric(right);
        if (leftNumeric && rightNumeric)
        {
            var leftDigits = left.TrimStart('0');
            var rightDigits = right.TrimStart('0');
            return leftDigits.Length != rightDigits.Length
                ? leftDigits.Length.CompareTo(rightDigits.Length)
                : string.CompareOrdinal(leftDigits, rightDigits);
        }

        if (leftNumeric != rightNumeric)
        {
            // Numeric identifiers have lower precedence than alphanumeric ones.
            return leftNumeric ? -1 : 1;
        }

        return string.CompareOrdinal(left, right);
    }

    private static bool IsNumeric(string identifier) =>
        identifier.Length > 0 && identifier.All(char.IsAsciiDigit);

    [GeneratedRegex(@"^v?(?<major>0|[1-9][0-9]*)\.(?<minor>0|[1-9][0-9]*)\.(?<patch>0|[1-9][0-9]*)(?:-(?<pre>" + _identifierPattern + @"))?(?:\+(?<build>" + _identifierPattern + @"))?$", RegexOptions.CultureInvariant)]
    private static partial Regex _versionRegex();
}
