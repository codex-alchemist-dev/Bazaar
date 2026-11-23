namespace Bazaar.Server.Souls.Presets;

/// <summary>What a preset validation rule rejected.</summary>
public enum PresetIssueCode
{
    /// <summary>The input is not valid JSON.</summary>
    InvalidJson,

    /// <summary>The root of a preset set is not a JSON array.</summary>
    NotAnArray,

    /// <summary>A JSON value has the wrong kind (for example a string where an object belongs).</summary>
    WrongType,

    /// <summary>A required key is missing.</summary>
    MissingKey,

    /// <summary>A key is not part of the preset schema.</summary>
    UnknownKey,

    /// <summary>The same property name appears twice in one object.</summary>
    DuplicateKey,

    /// <summary>Two presets in one set share an id.</summary>
    DuplicateId,

    /// <summary>A key names personal data, project content or content that export excludes.</summary>
    PersonalDataKey,

    /// <summary>A string value looks like personal data (an email address).</summary>
    PersonalDataValue,

    /// <summary>The preset id is not a valid slug.</summary>
    InvalidId,

    /// <summary>The schema version is not the one this build reads.</summary>
    UnsupportedSchemaVersion,

    /// <summary>A value is out of range, empty, malformed or repeated.</summary>
    InvalidValue,

    /// <summary>A route or whitelist entry is the global <c>auto</c> family. No global auto routing exists.</summary>
    AutoRoute,

    /// <summary>A route is neither a <c>custom/&lt;name&gt;</c> route nor a provider-qualified model.</summary>
    InvalidRoute,

    /// <summary>A provider-qualified route names a model that is not in the preset's whitelist.</summary>
    RouteNotWhitelisted,

    /// <summary>The whitelist is empty.</summary>
    EmptyWhitelist,

    /// <summary>A whitelist entry is not a provider-qualified model.</summary>
    InvalidWhitelistEntry,
}

/// <summary>One rejected rule.</summary>
/// <param name="Code">The rule that failed.</param>
/// <param name="Path">Where the problem is, as a JSON path such as <c>$[1].modelPolicy.route</c>.</param>
/// <param name="Message">Human-readable detail.</param>
public sealed record PresetIssue(PresetIssueCode Code, string Path, string Message);

/// <summary>
/// Raised when a preset set is rejected. Validation collects every issue it finds, so one throw reports them all.
/// Presets fail closed: one bad preset rejects the whole set.
/// </summary>
/// <param name="issues">The rejected rules. Must not be empty.</param>
public sealed class PresetValidationException(IReadOnlyList<PresetIssue> issues)
    : Exception(Summarize(issues))
{
    /// <summary>Every issue found in the set.</summary>
    public IReadOnlyList<PresetIssue> Issues { get; } = issues;

    private static string Summarize(IReadOnlyList<PresetIssue> issues) =>
        issues.Count == 0
            ? "Preset set rejected."
            : $"Preset set rejected with {issues.Count} issue(s): " + string.Join("; ", issues.Select(i => $"{i.Code} at {i.Path}: {i.Message}"));
}
