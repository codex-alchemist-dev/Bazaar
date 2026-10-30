using Bazaar.Contracts.Gc;

namespace Bazaar.Server.Calendar;

/// <summary>Calendar event kinds (docs/design/24 section 4). The stored text is the lower-case name.</summary>
public enum CalendarKind
{
    Vacation,
    Planning,
    Release,
    Freeze,
    Milestone,
}

/// <summary>Calendar event status. Cancelled is terminal.</summary>
public enum CalendarStatus
{
    Proposed,
    Confirmed,
    Cancelled,
}

/// <summary>The change a GC line announces.</summary>
public enum CalendarChangeKind
{
    Created,
    Updated,
    Confirmed,
    Cancelled,
}

/// <summary>What a calendar call decided. Only <see cref="Applied"/> changed anything.</summary>
public enum CalendarOutcome
{
    /// <summary>The change is committed and its GC line was appended.</summary>
    Applied,

    /// <summary>The request breaks a rule of the table or the calendar (inverted range, unknown zone, illegal transition).</summary>
    Invalid,

    /// <summary>The actor may not make this change under the policy.</summary>
    Refused,

    /// <summary>The expected version does not match the stored one.</summary>
    Conflict,

    /// <summary>No event has that id.</summary>
    NotFound,
}

/// <summary><c>calendar.vacation_confirmer</c> (doc 24 settings): who confirms a proposed vacation.</summary>
public enum VacationConfirmer
{
    Owner,
    ProjectLead,
}

/// <summary><c>calendar.release_creators</c> (doc 24 settings): who creates release and milestone events.</summary>
public enum ReleaseCreators
{
    Owner,
    OwnerOrProjectLead,
}

/// <summary>
/// The calendar's policy values, read from the settings store by the composition root. Defaults follow the doc 24 and doc 09
/// settings tables. Passing them in keeps the rules free of hard-coded choices.
/// </summary>
public sealed record CalendarPolicy
{
    /// <summary><c>calendar.vacation_confirmer</c>, default owner.</summary>
    public VacationConfirmer VacationConfirmer { get; init; } = VacationConfirmer.Owner;

    /// <summary><c>calendar.release_creators</c>, default owner or project lead.</summary>
    public ReleaseCreators ReleaseCreators { get; init; } = ReleaseCreators.OwnerOrProjectLead;

    /// <summary>
    /// <c>schedule.calendar_availability_kinds</c> (doc 09): kinds that block availability besides vacation. Default empty.
    /// Vacations always block availability.
    /// </summary>
    public IReadOnlySet<CalendarKind> AvailabilityKinds { get; init; } = new HashSet<CalendarKind>();
}

/// <summary>
/// Who asks for a change. The caller builds it from the authenticated session, never from client input.
/// <see cref="IsProjectLead"/> is true when the actor leads the project the event belongs to; the org graph (doc 24 section 2)
/// is not built yet, so the caller must set it from a checked source.
/// </summary>
/// <param name="Kind">Owner, agent or human member. Maps to the GC author kind.</param>
/// <param name="PrincipalId">The actor's principal (agent id for agents).</param>
/// <param name="IsProjectLead">True when the actor leads the event's project.</param>
public sealed record CalendarActor(AuthorKind Kind, Guid PrincipalId, bool IsProjectLead = false);

/// <summary>What a caller wants an event to say. The title is plain text here; it is sealed before it is stored.</summary>
public sealed record CalendarDraft
{
    public required CalendarKind Kind { get; init; }

    public required string Title { get; init; }

    /// <summary>Inclusive start instant.</summary>
    public required DateTimeOffset StartsAt { get; init; }

    /// <summary>Exclusive end instant. Must be after <see cref="StartsAt"/>.</summary>
    public required DateTimeOffset EndsAt { get; init; }

    /// <summary>When true, both instants must be local midnight in <see cref="Tz"/>.</summary>
    public bool AllDay { get; init; }

    /// <summary>IANA zone name.</summary>
    public required string Tz { get; init; }

    /// <summary>The agent the event is about. Required for a vacation.</summary>
    public Guid? AgentId { get; init; }

    /// <summary>The project the event belongs to, when there is one.</summary>
    public Guid? ProjectId { get; init; }

    /// <summary>The GC where the event was requested, when there is one (informational link).</summary>
    public Guid? SourceGcId { get; init; }
}

/// <summary>One stored calendar event. The title stays sealed; read it with <see cref="CalendarService.OpenTitle"/>.</summary>
public sealed record CalendarEventRecord
{
    public required Guid Id { get; init; }

    public required CalendarKind Kind { get; init; }

    public required Guid TitleKeyId { get; init; }

    public required byte[] TitleCiphertext { get; init; }

    public byte[]? TitleNonce { get; init; }

    public required DateTimeOffset StartsAt { get; init; }

    public required DateTimeOffset EndsAt { get; init; }

    public required bool AllDay { get; init; }

    public required string Tz { get; init; }

    public Guid? AgentId { get; init; }

    public Guid? ProjectId { get; init; }

    public required bool AffectsAvailability { get; init; }

    public required CalendarStatus Status { get; init; }

    public Guid? SourceGcId { get; init; }

    public required long Version { get; init; }
}

/// <summary>The answer to a calendar call. <see cref="Event"/> is the stored row after the call, when there is one.</summary>
/// <param name="Outcome">What happened.</param>
/// <param name="Event">The row after the change, or the current row on a refusal or conflict. Null when none exists.</param>
/// <param name="Reason">A short, plain reason for any outcome other than <see cref="CalendarOutcome.Applied"/>.</param>
public sealed record CalendarResult(CalendarOutcome Outcome, CalendarEventRecord? Event = null, string? Reason = null);
