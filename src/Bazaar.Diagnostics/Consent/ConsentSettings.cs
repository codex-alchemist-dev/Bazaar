namespace Bazaar.Diagnostics.Consent;

/// <summary>
/// The crash-report settings for one company (doc 43 section 7.3), with the documented defaults. Plain data: the
/// validator checks combinations and the decision function reads the mode.
/// </summary>
/// <remarks>
/// The floors in section 7.3 (redaction always on, no undeclared panes in screenshots, no screenshots to GitHub, and
/// the rest) have no fields here on purpose, so no setting can loosen them.
/// </remarks>
public sealed record ConsentSettings
{
    /// <summary>The company's mode. Default <see cref="ConsentMode.Ask"/>.</summary>
    public ConsentMode Mode { get; init; } = ConsentMode.Ask;

    /// <summary>Screenshot attachment. Default <see cref="ScreenshotMode.Off"/>.</summary>
    public ScreenshotMode Screenshot { get; init; } = ScreenshotMode.Off;

    /// <summary>Extra redaction patterns, added to the built-in rules. Default empty.</summary>
    public IReadOnlyList<string> RedactionExtraPatterns { get; init; } = [];

    /// <summary>Log lines included per report, 0 to 500. Default 200.</summary>
    public int LogLines { get; init; } = 200;

    /// <summary>Days a report is kept on the server, 1 to 365. Default 90.</summary>
    public int RetentionDays { get; init; } = 90;

    /// <summary>Days a screenshot is kept, 1 to 90 and at most <see cref="RetentionDays"/>. Default 30.</summary>
    public int ScreenshotRetentionDays { get; init; } = 30;

    /// <summary>Days an unsent report stays in the local queue, 1 to 30. Default 14.</summary>
    public int LocalQueueDays { get; init; } = 14;

    /// <summary>The GitHub repository for filed issues, as <c>owner/name</c>. Empty means filing is off. Default empty.</summary>
    public string IssueRepo { get; init; } = string.Empty;

    /// <summary>Issues filed per day, 0 to 200. Default 20.</summary>
    public int IssuesPerDay { get; init; } = 20;

    /// <summary>Server-side diagnosis mode. Default <see cref="ConsentMode.Ask"/>.</summary>
    public ConsentMode Diagnosis { get; init; } = ConsentMode.Ask;

    /// <summary>
    /// The diagnosis model, one of the whitelisted models for the diagnosis role. Null means setup has not resolved
    /// it yet; the validator refuses a null model while diagnosis is enabled.
    /// </summary>
    public string? DiagnosisModel { get; init; }

    /// <summary>Whether diagnoses are posted to the issue. Default <see cref="DiagnosisPostMode.Off"/>.</summary>
    public DiagnosisPostMode IssueDiagnosisPost { get; init; } = DiagnosisPostMode.Off;

    /// <summary>On-device NPU diagnosis (section 10). Default false.</summary>
    public bool NpuDiagnosis { get; init; }

    /// <summary>The shipped defaults, except <see cref="DiagnosisModel"/>, which setup resolves.</summary>
    public static ConsentSettings Default { get; } = new();
}
