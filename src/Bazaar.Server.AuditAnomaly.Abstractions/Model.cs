namespace Bazaar.Server.AuditAnomaly;

/// <summary>Event kinds the rules read. <c>data.read</c> is the read-audit row (doc 48 section 5.1).</summary>
public static class AnomalyEventKinds
{
    public const string DataRead = "data.read";
    public const string CanaryHit = "canary.hit";
    public const string ApprovalRequested = "approval.requested";
}

/// <summary>
/// One event on the stream the rules read. <see cref="Principal"/> is the agent id, <see cref="Count"/> the row count of a read
/// (1 for other kinds). No row content: only ids and labels.
/// </summary>
public sealed record AnomalyEvent(
    DateTimeOffset Ts, string Kind, string Principal, string? Session, string? ObjectType, string? ObjectId, long Count = 1);

/// <summary>Alert severity, lowest first.</summary>
public enum AnomalySeverity
{
    Low,
    Medium,
    High,
    Critical,
}

/// <summary>The deterministic rule kinds (doc 48 section 5.6).</summary>
public enum AnomalyRuleKind
{
    /// <summary>Sum of <c>data.read</c> counts on one object class reaches the threshold.</summary>
    ReadBurst,

    /// <summary>Count of reads the agent's scope does not allow reaches the threshold.</summary>
    ReadOutsideScope,

    /// <summary>Count of canary hits reaches the threshold.</summary>
    CanaryHit,

    /// <summary>Count of approval requests reaches the threshold.</summary>
    ApprovalFlood,
}

/// <summary>What a containment does. These are the kill-switch actions of doc 15; <see cref="FreezeWrites"/> is global only.</summary>
[Flags]
public enum ContainmentActions
{
    None = 0,
    RevokeLeases = 1,
    DetachMount = 2,
    BlockEgress = 4,
    PauseSessions = 8,
    FreezeWrites = 16,
}

/// <summary>How wide a containment is. Automatic containment never uses <see cref="Global"/>.</summary>
public enum ContainmentScope
{
    Session,
    Agent,
    Global,
}

/// <summary>What a containment applies to. <see cref="Id"/> is null only for <see cref="ContainmentScope.Global"/>.</summary>
public sealed record ContainmentTarget(ContainmentScope Scope, string? Id);

/// <summary>
/// A rule: threshold, window and severity, all operator settings. <see cref="ObjectClass"/> is required for read-burst.
/// <see cref="Actions"/> are applied automatically when the rule fires and automatic containment is on; they are at the
/// <see cref="AutoScope"/> (agent or session) only.
/// </summary>
public sealed record AnomalyRule(
    string Id,
    AnomalyRuleKind Kind,
    long Threshold,
    TimeSpan Window,
    AnomalySeverity Severity,
    bool Enabled = true,
    string? ObjectClass = null,
    ContainmentActions Actions = ContainmentActions.None,
    ContainmentScope AutoScope = ContainmentScope.Agent);

/// <summary>Who an alert is for.</summary>
[Flags]
public enum AlertAudience
{
    OwnerDevices = 1,
    SecurityReviewers = 2,
}

/// <summary>A fired rule. <see cref="ContainmentId"/> is set when auto-containment applied.</summary>
public sealed record AnomalyAlert(
    Guid Id, DateTimeOffset Ts, string RuleId, AnomalyRuleKind Kind, AnomalySeverity Severity, string Principal, string? Session,
    long Observed, long Threshold, TimeSpan Window, Guid? ContainmentId);

/// <summary>State of a containment.</summary>
public enum ContainmentState
{
    InForce,
    Undone,
}

/// <summary>One containment, with the end of its undo window.</summary>
public sealed record ContainmentRecord(
    Guid Id, ContainmentTarget Target, ContainmentActions Actions, string Reason, string AppliedBy, DateTimeOffset AppliedAt,
    DateTimeOffset UndoableUntil, ContainmentState State);

/// <summary>Who asks for a containment or an undo.</summary>
public sealed record Requester(string PrincipalId, bool IsOwner);

/// <summary>Result of a containment or undo request.</summary>
public enum ContainmentOutcome
{
    Done,
    RefusedNotOwner,
    RefusedInvalid,
    RefusedUnknown,
    RefusedAlreadyUndone,
    RefusedUndoWindowElapsed,
    RefusedBiometric,
    Failed,
}

/// <summary>Result with the record when there is one.</summary>
public sealed record ContainmentResult(ContainmentOutcome Outcome, ContainmentRecord? Record);
