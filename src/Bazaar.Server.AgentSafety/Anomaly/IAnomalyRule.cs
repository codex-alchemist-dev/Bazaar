namespace Bazaar.Server.AgentSafety.Anomaly;

/// <summary>
/// A deterministic anomaly rule (doc 48 section 5.6). A rule keeps its own per-agent window state, so it is fed every event in
/// order. <see cref="Evaluate"/> is called from many threads at once and must be safe for that.
/// </summary>
public interface IAnomalyRule
{
    /// <summary>The stable rule id, carried by every alert the rule raises.</summary>
    string Id { get; }

    /// <summary>Feeds one event to the rule and returns the alerts it raises, usually none.</summary>
    IReadOnlyList<AnomalyAlert> Evaluate(AnomalyEvent e);
}
