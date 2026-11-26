namespace Bazaar.Server.AgentSafety.Anomaly;

/// <summary>What an agent did that the anomaly rules count (doc 48 section 5.6).</summary>
public enum AnomalyEventKind
{
    /// <summary>The agent read an object of a classified kind, such as a personal object class.</summary>
    ObjectRead,

    /// <summary>The agent accessed something, and the access was checked against its scope.</summary>
    ScopedAccess,

    /// <summary>The agent touched a canary (a planted decoy, see doc 15).</summary>
    CanaryTouched,

    /// <summary>The agent asked for an approval (doc 15).</summary>
    ApprovalRequested,
}

/// <summary>
/// One observed event for the rule engine. It names the kind, the agent and session, and an object class at most. It never carries
/// content or secret values.
/// </summary>
/// <param name="Kind">What happened.</param>
/// <param name="AgentId">The agent that did it.</param>
/// <param name="SessionId">The session it happened in, when known.</param>
/// <param name="ObjectClass">The class of object involved, when the event has one.</param>
/// <param name="InScope">For <see cref="AnomalyEventKind.ScopedAccess"/>: whether the access was inside the agent's scope.</param>
/// <param name="At">When the event happened, from the producer's <see cref="TimeProvider"/>. The rules count by this time.</param>
public sealed record AnomalyEvent(
    AnomalyEventKind Kind,
    string AgentId,
    string? SessionId,
    string? ObjectClass,
    bool InScope,
    DateTimeOffset At);
