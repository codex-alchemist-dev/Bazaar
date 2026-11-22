namespace Bazaar.Server.Setup;

/// <summary>
/// The status of one verification check, and of the whole setup (doc 45 section 1.12). <see cref="NotEvaluated"/> is for a check
/// this build cannot run, because it needs a real service. It is never reported as green, so it can never make a setup look verified.
/// </summary>
public enum SetupCheckStatus
{
    Green,
    Amber,
    Red,
    NotEvaluated,
}

/// <summary>
/// What one check reports. <see cref="Message"/> names the check and the rule. It never carries a secret value or the text of a
/// secret reference. <see cref="Remediation"/> says what the operator can change, and is optional.
/// </summary>
public sealed record SetupCheckResult(SetupCheckStatus Status, string Message, string? Remediation = null)
{
    public static SetupCheckResult Green(string message) => new(SetupCheckStatus.Green, message);

    public static SetupCheckResult Amber(string message, string? remediation = null) => new(SetupCheckStatus.Amber, message, remediation);

    public static SetupCheckResult Red(string message, string? remediation = null) => new(SetupCheckStatus.Red, message, remediation);

    /// <summary>A check this build does not run. The reason says what is missing, for example "needs the Host adapter".</summary>
    public static SetupCheckResult NotEvaluated(string reason) => new(SetupCheckStatus.NotEvaluated, $"Not evaluated: {reason}.");
}

/// <summary>One check's row in the report: its id, its title and what it reported.</summary>
public sealed record SetupCheckReport(string Id, string Title, SetupCheckStatus Status, string Message, string? Remediation);

/// <summary>
/// The verification report (doc 45 section 1.12). <see cref="Overall"/> follows the rule in <c>SetupVerificationRules</c>:
/// any red is red; otherwise any amber or not-evaluated check is amber; otherwise green. <see cref="Score"/> is 0 to 100.
/// <see cref="AmberItems"/> lists every check that holds the overall status at amber (amber checks and not-evaluated checks),
/// in the order the checks ran. Setup completes only when each of them is acknowledged, see <see cref="CanComplete"/>.
/// </summary>
public sealed record SetupVerificationReport(
    SetupCheckStatus Overall,
    int Score,
    IReadOnlyList<SetupCheckReport> Checks,
    IReadOnlyList<SetupCheckReport> AmberItems)
{
    /// <summary>
    /// True when setup may be marked complete: green, or amber with every amber item named in <paramref name="acknowledgedCheckIds"/>.
    /// Red never completes. The acknowledgement itself is recorded by the caller, not by this report.
    /// </summary>
    public bool CanComplete(IEnumerable<string> acknowledgedCheckIds)
    {
        ArgumentNullException.ThrowIfNull(acknowledgedCheckIds);
        if (Overall == SetupCheckStatus.Red)
        {
            return false;
        }

        if (Overall == SetupCheckStatus.Green)
        {
            return true;
        }

        var acknowledged = new HashSet<string>(acknowledgedCheckIds, StringComparer.Ordinal);
        return AmberItems.All(item => acknowledged.Contains(item.Id));
    }
}
