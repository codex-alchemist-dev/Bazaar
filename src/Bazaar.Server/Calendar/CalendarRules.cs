using Bazaar.Contracts.Gc;

namespace Bazaar.Server.Calendar;

/// <summary>
/// Pure rules for the calendar (docs/design/24 section 4): draft validation, authority under the policy, and the status an
/// event starts with. No database and no clock here, so every rule is tested without Postgres.
/// </summary>
public static class CalendarRules
{
    /// <summary>Longest accepted title, in characters.</summary>
    public const int MaxTitleLength = 200;

    /// <summary>Longest accepted zone name, matching the table's CHECK.</summary>
    public const int MaxZoneLength = 100;

    /// <summary>Returns the reason a draft is refused, or null when it is valid.</summary>
    public static string? ValidateDraft(CalendarDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);

        if (!Enum.IsDefined(draft.Kind))
            return "Unknown calendar kind.";

        var title = draft.Title?.Trim() ?? string.Empty;
        if (title.Length == 0 || title.Length > MaxTitleLength)
            return $"Title must be 1 to {MaxTitleLength} characters.";

        var tz = draft.Tz?.Trim() ?? string.Empty;
        if (tz.Length == 0 || tz.Length > MaxZoneLength)
            return "Time zone is required.";
        if (!TryResolveZone(tz, out var zone))
            return $"Unknown time zone '{tz}'.";

        if (draft.EndsAt <= draft.StartsAt)
            return "An event must end after it starts.";

        if (draft.AllDay && !(IsLocalMidnight(draft.StartsAt, zone) && IsLocalMidnight(draft.EndsAt, zone)))
            return "An all-day event must start and end at local midnight in its time zone.";

        if (draft.Kind == CalendarKind.Vacation && draft.AgentId is null)
            return "A vacation must name an agent.";

        return null;
    }

    /// <summary>Resolves an IANA zone name. Returns false for an unknown name.</summary>
    public static bool TryResolveZone(string tz, out TimeZoneInfo zone)
    {
        try
        {
            zone = TimeZoneInfo.FindSystemTimeZoneById(tz);
            return true;
        }
        catch (TimeZoneNotFoundException)
        {
            zone = TimeZoneInfo.Utc;
            return false;
        }
        catch (InvalidTimeZoneException)
        {
            zone = TimeZoneInfo.Utc;
            return false;
        }
    }

    /// <summary>True when the instant is exactly midnight on the local clock of <paramref name="zone"/>.</summary>
    public static bool IsLocalMidnight(DateTimeOffset instant, TimeZoneInfo zone) =>
        TimeZoneInfo.ConvertTime(instant, zone).TimeOfDay == TimeSpan.Zero;

    /// <summary>
    /// Whether the event blocks availability: every vacation does, and other kinds only when the policy lists them
    /// (<c>schedule.calendar_availability_kinds</c>). The flag is computed here, never taken from a client.
    /// </summary>
    public static bool AffectsAvailability(CalendarKind kind, CalendarPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        return kind == CalendarKind.Vacation || policy.AvailabilityKinds.Contains(kind);
    }

    /// <summary>
    /// The status a new event starts with. A vacation starts confirmed when the actor may confirm it, otherwise proposed
    /// (an agent's request in its GC). Every other kind starts confirmed.
    /// </summary>
    public static CalendarStatus InitialStatus(CalendarActor actor, CalendarKind kind, CalendarPolicy policy)
    {
        if (kind != CalendarKind.Vacation)
            return CalendarStatus.Confirmed;
        return MayConfirm(actor, kind, policy) ? CalendarStatus.Confirmed : CalendarStatus.Proposed;
    }

    /// <summary>Authority to create an event of this kind for this agent.</summary>
    public static bool MayCreate(CalendarActor actor, CalendarKind kind, Guid? agentId, CalendarPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(actor);
        if (IsOwner(actor))
            return true;
        return kind switch
        {
            // An agent requests its own vacation; nobody else creates one for it.
            CalendarKind.Vacation => actor.Kind == AuthorKind.Agent && agentId == actor.PrincipalId,
            CalendarKind.Release or CalendarKind.Milestone => IsReleaseCreator(actor, policy),
            // Planning and freeze events are owner-only. This is this build's reading; the owner may widen it.
            _ => false,
        };
    }

    /// <summary>Authority to change an existing, not cancelled event. Confirmed vacations are changed by the owner only.</summary>
    public static bool MayModify(CalendarActor actor, CalendarKind kind, Guid? agentId, CalendarStatus status, CalendarPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(actor);
        if (IsOwner(actor))
            return true;
        return kind switch
        {
            CalendarKind.Vacation => actor.Kind == AuthorKind.Agent && agentId == actor.PrincipalId && status == CalendarStatus.Proposed,
            CalendarKind.Release or CalendarKind.Milestone => IsReleaseCreator(actor, policy),
            _ => false,
        };
    }

    /// <summary>Authority to confirm a proposed vacation under <c>calendar.vacation_confirmer</c>.</summary>
    public static bool MayConfirm(CalendarActor actor, CalendarKind kind, CalendarPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(actor);
        if (kind != CalendarKind.Vacation)
            return false;
        if (IsOwner(actor))
            return true;
        return policy.VacationConfirmer == VacationConfirmer.ProjectLead && actor.IsProjectLead;
    }

    /// <summary>Authority to cancel: the owner, the vacation's own agent, or a release creator for release and milestone events.</summary>
    public static bool MayCancel(CalendarActor actor, CalendarKind kind, Guid? agentId, CalendarPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(actor);
        if (IsOwner(actor))
            return true;
        return kind switch
        {
            CalendarKind.Vacation => actor.Kind == AuthorKind.Agent && agentId == actor.PrincipalId,
            CalendarKind.Release or CalendarKind.Milestone => IsReleaseCreator(actor, policy),
            _ => false,
        };
    }

    /// <summary>Stored text for a kind. Matches the CHECK constraint in 0017.</summary>
    public static string ToSql(CalendarKind kind) => kind switch
    {
        CalendarKind.Vacation => "vacation",
        CalendarKind.Planning => "planning",
        CalendarKind.Release => "release",
        CalendarKind.Freeze => "freeze",
        CalendarKind.Milestone => "milestone",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    /// <summary>Parses stored kind text. Throws for an unknown value, which the CHECK constraint prevents.</summary>
    public static CalendarKind KindFromSql(string text) => text switch
    {
        "vacation" => CalendarKind.Vacation,
        "planning" => CalendarKind.Planning,
        "release" => CalendarKind.Release,
        "freeze" => CalendarKind.Freeze,
        "milestone" => CalendarKind.Milestone,
        _ => throw new ArgumentOutOfRangeException(nameof(text), text, "Unknown stored calendar kind."),
    };

    /// <summary>Stored text for a status. Matches the CHECK constraint in 0017.</summary>
    public static string ToSql(CalendarStatus status) => status switch
    {
        CalendarStatus.Proposed => "proposed",
        CalendarStatus.Confirmed => "confirmed",
        CalendarStatus.Cancelled => "cancelled",
        _ => throw new ArgumentOutOfRangeException(nameof(status)),
    };

    /// <summary>Parses stored status text.</summary>
    public static CalendarStatus StatusFromSql(string text) => text switch
    {
        "proposed" => CalendarStatus.Proposed,
        "confirmed" => CalendarStatus.Confirmed,
        "cancelled" => CalendarStatus.Cancelled,
        _ => throw new ArgumentOutOfRangeException(nameof(text), text, "Unknown stored calendar status."),
    };

    /// <summary>Stored text for a change, as it appears in the GC line payload.</summary>
    public static string ToText(CalendarChangeKind change) => change switch
    {
        CalendarChangeKind.Created => "created",
        CalendarChangeKind.Updated => "updated",
        CalendarChangeKind.Confirmed => "confirmed",
        CalendarChangeKind.Cancelled => "cancelled",
        _ => throw new ArgumentOutOfRangeException(nameof(change)),
    };

    /// <summary>The GC source trust that matches an author kind. Calendar actors are owner, agent, human or system.</summary>
    public static SourceTrust SourceTrustFor(AuthorKind kind) => kind switch
    {
        AuthorKind.Owner => SourceTrust.Owner,
        AuthorKind.Agent => SourceTrust.Agent,
        AuthorKind.Human => SourceTrust.MemberHuman,
        AuthorKind.System => SourceTrust.System,
        _ => throw new ArgumentException("Calendar changes come from an owner, agent, human or system.", nameof(kind)),
    };

    private static bool IsOwner(CalendarActor actor) => actor.Kind == AuthorKind.Owner;

    private static bool IsReleaseCreator(CalendarActor actor, CalendarPolicy policy) =>
        policy.ReleaseCreators == ReleaseCreators.OwnerOrProjectLead && actor.IsProjectLead;
}
