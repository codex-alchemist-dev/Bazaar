namespace Bazaar.Server.Calendar;

/// <summary>The Company Calendar's writer (docs/design/24 section 4). The only code that changes <c>calendar_event</c>.</summary>
public interface ICalendarService
{
    /// <summary>Creates an event. A vacation starts proposed unless the actor may confirm it.</summary>
    Task<CalendarResult> CreateAsync(CalendarDraft draft, CalendarActor actor, Guid noticeGcId, CancellationToken ct = default);

    /// <summary>Writes a new version of an event if <paramref name="expectedVersion"/> still matches. The status is kept.</summary>
    Task<CalendarResult> UpdateAsync(Guid id, long expectedVersion, CalendarDraft draft, CalendarActor actor, Guid noticeGcId, CancellationToken ct = default);

    /// <summary>Confirms a proposed vacation under <c>calendar.vacation_confirmer</c>.</summary>
    Task<CalendarResult> ConfirmAsync(Guid id, long expectedVersion, CalendarActor actor, Guid noticeGcId, CancellationToken ct = default);

    /// <summary>Cancels an event. Cancel is a status change; the row stays.</summary>
    Task<CalendarResult> CancelAsync(Guid id, long expectedVersion, CalendarActor actor, Guid noticeGcId, CancellationToken ct = default);

    /// <summary>Reads one event. The title stays sealed; see <c>CalendarService.OpenTitleAsync</c>.</summary>
    Task<CalendarEventRecord?> GetAsync(Guid id, CancellationToken ct = default);

    /// <summary>Lists events overlapping [<paramref name="from"/>, <paramref name="until"/>), including cancelled rows.</summary>
    Task<IReadOnlyList<CalendarEventRecord>> ListOverlappingAsync(DateTimeOffset from, DateTimeOffset until, Guid? agentId, CancellationToken ct = default);
}
