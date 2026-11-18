namespace Bazaar.Diagnostics.Consent;

/// <summary>
/// The company's crash-report mode (doc 43 section 7.1). Off is the most protective value and the floor: nothing
/// is captured beyond the normal local log, and nothing leaves the device.
/// </summary>
public enum ConsentMode
{
    /// <summary>Nothing is built or sent. No dialog and no notice.</summary>
    Off,

    /// <summary>Reports are built and queued. The person decides each one. The per-company default.</summary>
    Ask,

    /// <summary>Reports are built, redacted and sent without a dialog. The person sees a notice afterwards.</summary>
    Auto,
}

/// <summary>Whether a screenshot may accompany a report (doc 43 section 7.3, <c>telemetry.crash.screenshot</c>).</summary>
public enum ScreenshotMode
{
    /// <summary>Screenshots are never attached.</summary>
    Off,

    /// <summary>The person decides per report, and the checkbox is unticked.</summary>
    Ask,

    /// <summary>The checkbox is ticked; the person may untick it.</summary>
    Allowed,
}

/// <summary>Whether a diagnosis is posted to the GitHub issue (doc 43 section 7.3, <c>telemetry.crash.issue_diagnosis_post</c>).</summary>
public enum DiagnosisPostMode
{
    /// <summary>Diagnoses stay on the server.</summary>
    Off,

    /// <summary>Diagnoses are posted to the issue, labelled "unverified diagnosis".</summary>
    On,
}

/// <summary>What the device does next for a crash report (doc 43 section 7.1).</summary>
public enum ConsentAction
{
    /// <summary>Send nothing and show nothing.</summary>
    SendNothing,

    /// <summary>Keep the report in the local queue and show the consent dialog.</summary>
    PromptPerson,

    /// <summary>Redact and send the report without a dialog.</summary>
    SendAutomatically,
}
