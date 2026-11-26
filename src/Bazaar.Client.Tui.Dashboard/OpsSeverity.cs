namespace Bazaar.Client.Tui.Dashboard;

/// <summary>How the resident TUI treats a Kodo signal (doc 58 section 3a): serious gets a banner and a bell, minor only an amber line.</summary>
public enum OpsSeverity
{
    Ok,
    Minor,
    Serious,
}

/// <summary>
/// Maps Kodo's grades (doc 46) to <see cref="OpsSeverity"/>. The web admin ports carry both as strings, so the match is by word, case
/// insensitive. Failing, frozen and critical are serious; degraded and warning are minor; healthy, recovering and info are fine.
/// A state the TUI does not know is shown as minor (amber, with its own word): an unknown signal must never read as green.
/// </summary>
public static class KodoSeverity
{
    public static OpsSeverity FromState(string? state) => Classify(state?.Trim().ToLowerInvariant());

    /// <summary>An incident's severity word (doc 46 section 16: critical, warning, info).</summary>
    public static OpsSeverity FromIncident(string? severity) => Classify(severity?.Trim().ToLowerInvariant());

    private static OpsSeverity Classify(string? word) => word switch
    {
        "failing" or "frozen" or "critical" => OpsSeverity.Serious,
        "healthy" or "recovering" or "info" or "informational" or "ok" => OpsSeverity.Ok,
        _ => OpsSeverity.Minor,
    };

    /// <summary>The word shown next to a line, so colour is never the only signal (doc 58 section 4).</summary>
    public static string Label(OpsSeverity severity) => severity switch
    {
        OpsSeverity.Serious => "SERIOUS",
        OpsSeverity.Minor => "minor",
        _ => "ok",
    };
}
