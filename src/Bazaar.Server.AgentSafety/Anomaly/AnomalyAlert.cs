namespace Bazaar.Server.AgentSafety.Anomaly;

/// <summary>How serious an anomaly rule's finding is (doc 48 section 5.6). Each rule has a fixed severity, set by its settings.</summary>
public enum AnomalySeverity
{
    Low,
    Medium,
    High,
    Critical,
}

/// <summary>
/// One finding of a deterministic anomaly rule (doc 48 section 5.6): which rule fired, for which agent and session, how serious it is,
/// when, and a short human-readable reason. The detail never carries secret values or message content.
/// </summary>
/// <param name="RuleId">Stable rule id, for example <c>read_burst</c>, <c>out_of_scope_read</c>, <c>canary_hit</c> or <c>approval_flood</c>.</param>
/// <param name="AgentId">The agent the finding is about.</param>
/// <param name="SessionId">The session the finding is about, or null when the rule is about the agent as a whole.</param>
/// <param name="Severity">How serious the finding is.</param>
/// <param name="At">When the rule fired, from the engine's <see cref="TimeProvider"/>.</param>
/// <param name="Detail">A short reason for the owner and the audit log.</param>
public sealed record AnomalyAlert(string RuleId, string AgentId, string? SessionId, AnomalySeverity Severity, DateTimeOffset At, string Detail);

/// <summary>Where alerts come from. The containment service reads from it; the rule engine writes to it.</summary>
public interface IAnomalyAlertSource
{
    /// <summary>Yields every alert as it is raised, until <paramref name="ct"/> is cancelled.</summary>
    IAsyncEnumerable<AnomalyAlert> ReadAllAsync(CancellationToken ct);
}

/// <summary>Where the rule engine publishes an alert once a rule fires.</summary>
public interface IAnomalyAlertSink
{
    ValueTask PublishAsync(AnomalyAlert alert, CancellationToken ct);
}
