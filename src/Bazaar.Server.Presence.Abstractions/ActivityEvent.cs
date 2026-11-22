using Bazaar.Server.WorkLife;

namespace Bazaar.Server.Presence.Abstractions;

/// <summary>What an <see cref="ActivityEvent"/> reports (docs/design/23, "Event sources and subscription").</summary>
public enum ActivityEventKind
{
    /// <summary>An availability transition (doc 09).</summary>
    AvailabilityChange,

    /// <summary>A turn or tool lifecycle event (doc 06): the current activity, tool class and GC.</summary>
    Turn,

    /// <summary>A quota or throttle change (doc 05, doc 09): the THROTTLED overlay turned on or off.</summary>
    QuotaThrottle,

    /// <summary>The current turn waits on a Kanban prerequisite (doc 12), or stopped waiting.</summary>
    KanbanWait,

    /// <summary>The current turn waits on an approval, or stopped waiting.</summary>
    ApprovalWait,

    /// <summary>A cover started or stopped answering for the agent (doc 09): the COVERED overlay.</summary>
    Covered,

    /// <summary>The agent entered or left the Psych Ward (doc 26): the WARD overlay.</summary>
    Ward,
}

/// <summary>
/// One input to the activity projector. <see cref="SourceTime"/> is when the source says it happened, so the projector can drop
/// duplicates and events that arrive late (doc 23: events older than the last applied source time are dropped).
/// </summary>
/// <param name="AgentId">The agent the event is about.</param>
/// <param name="SourceTime">When the source recorded the event (UTC).</param>
/// <param name="Kind">What the event reports.</param>
public sealed record ActivityEvent(Guid AgentId, DateTimeOffset SourceTime, ActivityEventKind Kind)
{
    /// <summary>The new availability. Read for <see cref="ActivityEventKind.AvailabilityChange"/>.</summary>
    public PresenceAvailability? Availability { get; init; }

    /// <summary>The turn's activity. Read for <see cref="ActivityEventKind.Turn"/>; <see cref="PresenceActivityKind.Idle"/> ends the turn.</summary>
    public PresenceActivityKind? Activity { get; init; }

    /// <summary>The tool class, for a tool activity.</summary>
    public PresenceToolClass ToolClass { get; init; }

    /// <summary>The kind of GC the turn runs in.</summary>
    public PresenceGcContext GcContext { get; init; }

    /// <summary>The GC the turn runs in, learned from the <c>gc</c> NOTIFY (doc 04).</summary>
    public Guid? GcId { get; init; }

    /// <summary>The turn.</summary>
    public Guid? TurnId { get; init; }

    /// <summary>A model-written activity label. The projector truncates it to 80 characters and drops it when the operator allows kinds only.</summary>
    public string? Label { get; init; }

    /// <summary>For the flag kinds (throttle, waits, covered, ward): true when the condition began, false when it ended.</summary>
    public bool Active { get; init; } = true;

    /// <summary>An availability transition.</summary>
    public static ActivityEvent AvailabilityChanged(Guid agentId, DateTimeOffset at, PresenceAvailability availability) =>
        new(agentId, at, ActivityEventKind.AvailabilityChange) { Availability = availability };

    /// <summary>A turn or tool lifecycle event. Pass <see cref="PresenceActivityKind.Idle"/> when the turn ended.</summary>
    public static ActivityEvent TurnChanged(
        Guid agentId,
        DateTimeOffset at,
        PresenceActivityKind activity,
        PresenceToolClass toolClass = PresenceToolClass.None,
        PresenceGcContext gcContext = PresenceGcContext.Other,
        Guid? gcId = null,
        Guid? turnId = null,
        string? label = null) =>
        new(agentId, at, ActivityEventKind.Turn)
        {
            Activity = activity,
            ToolClass = toolClass,
            GcContext = gcContext,
            GcId = gcId,
            TurnId = turnId,
            Label = label,
        };

    /// <summary>A flag event of <paramref name="kind"/>.</summary>
    public static ActivityEvent Flag(Guid agentId, DateTimeOffset at, ActivityEventKind kind, bool active) =>
        new(agentId, at, kind) { Active = active };
}
