namespace Bazaar.Diagnostics.Consent;

/// <summary>
/// Registry keys for the crash-report settings (doc 43 section 7.3). The registry itself is owned by doc 03 and is
/// not part of this package; these names are what it stores.
/// </summary>
public static class ConsentSettingKeys
{
    public const string Mode = "telemetry.crash.mode";
    public const string Screenshot = "telemetry.crash.screenshot";
    public const string RedactionExtraPatterns = "telemetry.crash.redaction_extra_patterns";
    public const string LogLines = "telemetry.crash.log_lines";
    public const string RetentionDays = "telemetry.crash.retention_days";
    public const string ScreenshotRetentionDays = "telemetry.crash.screenshot_retention_days";
    public const string LocalQueueDays = "telemetry.crash.local_queue_days";
    public const string IssueRepo = "telemetry.crash.issue_repo";
    public const string IssuesPerDay = "telemetry.crash.issues_per_day";
    public const string Diagnosis = "telemetry.crash.diagnosis";
    public const string DiagnosisModel = "telemetry.crash.diagnosis_model";
    public const string IssueDiagnosisPost = "telemetry.crash.issue_diagnosis_post";
    public const string NpuDiagnosis = "telemetry.crash.npu_diagnosis";

    /// <summary>Every key in this model, in the order of doc 43 section 7.3.</summary>
    public static IReadOnlyList<string> All { get; } =
    [
        Mode,
        Screenshot,
        RedactionExtraPatterns,
        LogLines,
        RetentionDays,
        ScreenshotRetentionDays,
        LocalQueueDays,
        IssueRepo,
        IssuesPerDay,
        Diagnosis,
        DiagnosisModel,
        IssueDiagnosisPost,
        NpuDiagnosis,
    ];
}
