using System.Text.RegularExpressions;

namespace Bazaar.Server.Modules.Deps;

/// <summary>
/// Validates dependency manifests (docs/design/42-deps-as-modules.md sections 5, 8.4, 13.1). Pure: no I/O, no exceptions for bad
/// input. The licence verdict comes in as data; this class does not decide licence facts.
/// </summary>
public static partial class DependencyManifestValidator
{
    private const int _maxIdLength = 64;

    /// <summary>Validates one manifest and returns every issue found, errors and warnings together. Empty means clean.</summary>
    public static IReadOnlyList<DependencyManifestIssue> Validate(DependencyManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        var issues = new List<DependencyManifestIssue>();

        if (!IsSafeSlug(manifest.Id))
        {
            Add(issues, DependencyManifestIssueCode.InvalidModuleId, DependencyManifestIssueSeverity.Error,
                $"Module id '{manifest.Id}' is not a safe slug (lowercase letters, digits and single hyphens, up to {_maxIdLength} characters).");
        }

        if (string.IsNullOrWhiteSpace(manifest.DisplayName))
        {
            Add(issues, DependencyManifestIssueCode.MissingDisplayName, DependencyManifestIssueSeverity.Error,
                $"Module '{manifest.Id}' has no display name.");
        }

        foreach (var required in DependencyManifest.RequiredTags.Where(t => !manifest.Tags.Contains(t, StringComparer.Ordinal)))
        {
            Add(issues, DependencyManifestIssueCode.MissingRequiredTag, DependencyManifestIssueSeverity.Error,
                $"Module '{manifest.Id}' is missing the required tag '{required}'.");
        }

        ValidateLines(manifest, issues);
        ValidateExtensions(manifest, issues);
        ValidateLicence(manifest, issues);

        return issues;
    }

    /// <summary>
    /// Validates a set of manifests together. Each manifest is validated on its own, and every module id must appear once,
    /// because two lines of one module belong in one manifest (D2).
    /// </summary>
    public static IReadOnlyList<DependencyManifestIssue> ValidateCatalogue(IEnumerable<DependencyManifest> manifests)
    {
        ArgumentNullException.ThrowIfNull(manifests);

        var issues = new List<DependencyManifestIssue>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var reported = new HashSet<string>(StringComparer.Ordinal);

        foreach (var manifest in manifests)
        {
            ArgumentNullException.ThrowIfNull(manifest);
            issues.AddRange(Validate(manifest));

            if (!seen.Add(manifest.Id) && reported.Add(manifest.Id))
            {
                Add(issues, DependencyManifestIssueCode.DuplicateModuleId, DependencyManifestIssueSeverity.Error,
                    $"Module id '{manifest.Id}' appears more than once. Two lines of one module belong in one manifest.");
            }
        }

        return issues;
    }

    /// <summary>True when no issue is an error. Warnings do not block an install.</summary>
    public static bool CanInstall(IEnumerable<DependencyManifestIssue> issues)
    {
        ArgumentNullException.ThrowIfNull(issues);

        return !issues.Any(i => i.Severity == DependencyManifestIssueSeverity.Error);
    }

    /// <summary>Whether an id is a safe slug: lowercase letters and digits in hyphen-separated runs, at most 64 characters.</summary>
    public static bool IsSafeSlug(string? value) =>
        value is not null && value.Length <= _maxIdLength && SlugPattern().IsMatch(value);

    private static void ValidateLines(DependencyManifest manifest, List<DependencyManifestIssue> issues)
    {
        if (manifest.Lines.Count == 0)
        {
            Add(issues, DependencyManifestIssueCode.NoLines, DependencyManifestIssueSeverity.Error,
                $"Module '{manifest.Id}' has no version lines.");
            return;
        }

        var lineIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in manifest.Lines)
        {
            if (!LinePattern().IsMatch(line.Line))
            {
                Add(issues, DependencyManifestIssueCode.InvalidLineId, DependencyManifestIssueSeverity.Error,
                    $"Module '{manifest.Id}' has line id '{line.Line}', which is not digits only.");
            }
            else if (!lineIds.Add(line.Line))
            {
                Add(issues, DependencyManifestIssueCode.DuplicateLineId, DependencyManifestIssueSeverity.Error,
                    $"Module '{manifest.Id}' lists line '{line.Line}' more than once.");
            }

            if (!VersionPattern().IsMatch(line.Version))
            {
                Add(issues, DependencyManifestIssueCode.InvalidVersion, DependencyManifestIssueSeverity.Error,
                    $"Module '{manifest.Id}' line '{line.Line}' has version '{line.Version}', which is not a three-part SemVer.");
            }
        }

        var newest = manifest.Lines.Where(l => l.Role == DependencyLineRole.Newest).ToList();
        if (newest.Count != 1)
        {
            Add(issues, DependencyManifestIssueCode.NewestLineCount, DependencyManifestIssueSeverity.Error,
                $"Module '{manifest.Id}' must have exactly one line with role newest, found {newest.Count}.");
        }

        var stableCount = manifest.Lines.Count(l => l.Role == DependencyLineRole.Stable);
        if (stableCount > 1)
        {
            Add(issues, DependencyManifestIssueCode.MultipleStableLines, DependencyManifestIssueSeverity.Error,
                $"Module '{manifest.Id}' has {stableCount} lines with role stable. At most one is allowed.");
        }

        // A retired line must not be the newest: no retired line may be ordered at or above the newest line.
        if (newest.Count == 1 && LineNumber(newest[0].Line) is { } newestNumber)
        {
            foreach (var retired in manifest.Lines.Where(l => l.Role == DependencyLineRole.Retired))
            {
                if (LineNumber(retired.Line) is { } retiredNumber && retiredNumber >= newestNumber)
                {
                    Add(issues, DependencyManifestIssueCode.RetiredLineIsNewest, DependencyManifestIssueSeverity.Error,
                        $"Module '{manifest.Id}' line '{retired.Line}' is retired but is not below the newest line '{newest[0].Line}'.");
                }
            }
        }
    }

    private static void ValidateExtensions(DependencyManifest manifest, List<DependencyManifestIssue> issues)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var extension in manifest.Extensions)
        {
            if (!IsSafeSlug(extension))
            {
                Add(issues, DependencyManifestIssueCode.InvalidExtensionName, DependencyManifestIssueSeverity.Error,
                    $"Module '{manifest.Id}' lists extension '{extension}', which is not a safe slug.");
            }
            else if (!names.Add(extension))
            {
                Add(issues, DependencyManifestIssueCode.DuplicateExtension, DependencyManifestIssueSeverity.Error,
                    $"Module '{manifest.Id}' lists extension '{extension}' more than once.");
            }
        }
    }

    private static void ValidateLicence(DependencyManifest manifest, List<DependencyManifestIssue> issues)
    {
        if (string.IsNullOrWhiteSpace(manifest.LicenceSpdx))
        {
            Add(issues, DependencyManifestIssueCode.MissingLicence, DependencyManifestIssueSeverity.Error,
                $"Module '{manifest.Id}' has no licence identifier.");
        }

        switch (manifest.LicenceVerdict)
        {
            case DependencyLicenceVerdict.Incompatible:
                Add(issues, DependencyManifestIssueCode.IncompatibleLicence, DependencyManifestIssueSeverity.Error,
                    $"Module '{manifest.Id}' is not AGPL-compatible ({manifest.LicenceSpdx}). Dependencies have no override path.");
                break;

            case DependencyLicenceVerdict.Unverified:
                Add(issues, DependencyManifestIssueCode.UnverifiedLicence, DependencyManifestIssueSeverity.Warning,
                    $"Module '{manifest.Id}' licence ({manifest.LicenceSpdx}) is not verified yet. It installs with a warning.");
                break;

            case DependencyLicenceVerdict.Compatible:
                break;

            default:
                // An out-of-range verdict is not a licence fact we know; refuse it rather than guess.
                Add(issues, DependencyManifestIssueCode.IncompatibleLicence, DependencyManifestIssueSeverity.Error,
                    $"Module '{manifest.Id}' has an unknown licence verdict '{manifest.LicenceVerdict}'.");
                break;
        }
    }

    private static int? LineNumber(string line) =>
        LinePattern().IsMatch(line) && int.TryParse(line, System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture, out var number)
            ? number
            : null;

    private static void Add(List<DependencyManifestIssue> issues, DependencyManifestIssueCode code,
        DependencyManifestIssueSeverity severity, string message) =>
        issues.Add(new DependencyManifestIssue(code, severity, message));

    [GeneratedRegex("^[a-z0-9]+(-[a-z0-9]+)*$", RegexOptions.CultureInvariant)]
    private static partial Regex SlugPattern();

    [GeneratedRegex("^[0-9]{1,4}$", RegexOptions.CultureInvariant)]
    private static partial Regex LinePattern();

    [GeneratedRegex("^[0-9]+\\.[0-9]+\\.[0-9]+$", RegexOptions.CultureInvariant)]
    private static partial Regex VersionPattern();
}
