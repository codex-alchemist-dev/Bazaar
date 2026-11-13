namespace Bazaar.Server.Modules.Deps;

/// <summary>One finding from validating a dependency manifest. Validation returns these; it never throws for bad input.</summary>
/// <param name="Code">What rule was broken.</param>
/// <param name="Severity">Errors block the install. Warnings install with a warning.</param>
/// <param name="Message">A sentence naming the module, line or field involved.</param>
public sealed record DependencyManifestIssue(
    DependencyManifestIssueCode Code,
    DependencyManifestIssueSeverity Severity,
    string Message);

/// <summary>Whether an issue blocks the install.</summary>
public enum DependencyManifestIssueSeverity
{
    /// <summary>Blocks the install.</summary>
    Error,

    /// <summary>Installs with a warning shown to the user.</summary>
    Warning,
}

/// <summary>The rule a dependency manifest issue reports.</summary>
public enum DependencyManifestIssueCode
{
    /// <summary>The module id is not a safe slug.</summary>
    InvalidModuleId,

    /// <summary>The display name is empty.</summary>
    MissingDisplayName,

    /// <summary>A required tag (<c>Deps</c> or <c>3rd party</c>) is missing (section 8.4, rule 4).</summary>
    MissingRequiredTag,

    /// <summary>The module has no version lines.</summary>
    NoLines,

    /// <summary>A line id is not digits only.</summary>
    InvalidLineId,

    /// <summary>Two lines of the same module have the same line id.</summary>
    DuplicateLineId,

    /// <summary>A line version is not a normalised three-part SemVer.</summary>
    InvalidVersion,

    /// <summary>The module does not have exactly one line with role newest.</summary>
    NewestLineCount,

    /// <summary>The module has more than one line with role stable.</summary>
    MultipleStableLines,

    /// <summary>A retired line is ordered at or above the newest line.</summary>
    RetiredLineIsNewest,

    /// <summary>An extension name is not a safe slug.</summary>
    InvalidExtensionName,

    /// <summary>The same extension is listed twice.</summary>
    DuplicateExtension,

    /// <summary>The licence identifier is empty.</summary>
    MissingLicence,

    /// <summary>The licence is not AGPL-compatible. No override path exists (D8).</summary>
    IncompatibleLicence,

    /// <summary>The licence is not yet verified. The dependency installs with a warning.</summary>
    UnverifiedLicence,

    /// <summary>Two manifests in one catalogue share a module id. Two lines of one module are one manifest instead (D2).</summary>
    DuplicateModuleId,

    /// <summary>A required line is not among the installed lines of the module.</summary>
    MissingLine,
}
