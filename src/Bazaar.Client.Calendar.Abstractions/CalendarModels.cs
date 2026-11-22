namespace Bazaar.Client.Calendar;

/// <summary>Kind of a company calendar event (doc 24 section 4). Only vacation changes availability by default.</summary>
public enum CalendarEventKind
{
    Vacation,
    Planning,
    Release,
    Freeze,
    Milestone,
}

/// <summary>Lifecycle of a calendar event. Cancel is a status change, never a delete.</summary>
public enum CalendarEventStatus
{
    Proposed,
    Confirmed,
    Cancelled,
}

/// <summary>Which zone the calendar uses to place timed events on a day (setting <c>calendar.display_timezone</c>).</summary>
public enum CalendarDisplayTimezone
{
    /// <summary>The viewer's local zone.</summary>
    Viewer,

    /// <summary>The event's own zone.</summary>
    Agent,
}

/// <summary>
/// One calendar event as the pane shows it. <paramref name="Title"/> is a personal field: never log it.
/// <paramref name="Tz"/> is an IANA or Windows zone id and may be unknown to the client.
/// </summary>
public sealed record CalendarEventSummary(
    string Id,
    CalendarEventKind Kind,
    string Title,
    DateTimeOffset StartsAt,
    DateTimeOffset EndsAt,
    bool AllDay,
    string Tz,
    string? AgentId,
    string? ProjectId,
    bool AffectsAvailability,
    CalendarEventStatus Status);

/// <summary>The events one calendar pane shows, plus the zone mode.</summary>
public sealed record CalendarSnapshot(CalendarDisplayTimezone DisplayTimezone, IReadOnlyList<CalendarEventSummary> Events);

/// <summary>Optional source of calendar snapshots for a head that does not feed the pane through its model.</summary>
public interface ICalendarSnapshotSource
{
    ValueTask<CalendarSnapshot> GetSnapshotAsync(CancellationToken cancellationToken);
}
