namespace Bazaar.Diagnostics.Consent;

/// <summary>
/// The device's next step for a crash report, from the company mode (doc 43 section 7.1). Pure. An unknown mode
/// falls back to <see cref="ConsentAction.SendNothing"/>, the most protective choice, so a corrupt value never sends.
/// </summary>
public static class ConsentDecision
{
    /// <summary>
    /// Returns what happens for a report. <paramref name="crashReportExists"/> is true when a report, or the minimal
    /// report of section 7.5, has been built. Without one, nothing happens in any mode.
    /// </summary>
    public static ConsentAction Decide(ConsentSettings settings, bool crashReportExists)
    {
        ArgumentNullException.ThrowIfNull(settings);

        if (!crashReportExists)
        {
            return ConsentAction.SendNothing;
        }

        return settings.Mode switch
        {
            ConsentMode.Ask => ConsentAction.PromptPerson,
            ConsentMode.Auto => ConsentAction.SendAutomatically,
            _ => ConsentAction.SendNothing,
        };
    }
}
