using System.Collections.Immutable;

namespace Bazaar.Server.WorkLife;

/// <summary>
/// Pure availability calculator (doc 09, WP1). It reads no clock and does no I/O: the caller passes
/// <c>now</c>, so results are reproducible and testable.
/// </summary>
/// <remarks>
/// Precedence: vacation beats curfew, curfew beats shift; outside every window the agent is
/// <see cref="AvailabilityState.Off"/>. Shifts and curfew are read in the agent's zone and may cross midnight.
/// Candidate windows are searched on the local days before, on, and after <c>now</c>, so a window that
/// crosses midnight is found from either side.
///
/// DST rules for local wall-clock times in the agent's zone:
/// <list type="bullet">
/// <item>
/// A local time that does not exist (spring-forward gap) is taken as the instant that the offset in effect
/// before the change gives. The time therefore moves forward by the length of the gap: a shift that opens at
/// 02:30 in a 02:00 to 03:00 gap opens at 03:30 local time.
/// </item>
/// <item>
/// An ambiguous local time (fall-back overlap) takes its first occurrence, the earlier instant, which is the
/// occurrence with the offset in effect before the change.
/// </item>
/// </list>
/// </remarks>
public static class ScheduleEvaluator
{
    /// <summary>How far ahead <see cref="AvailabilityWindow.NextChangeAt"/> is searched.</summary>
    private static readonly TimeSpan _horizon = TimeSpan.FromDays(8);

    /// <summary>
    /// Returns the availability at <paramref name="now"/>, plus the next boundary within 8 days.
    /// </summary>
    /// <param name="schedule">The agent's shifts and curfew, with its zone.</param>
    /// <param name="events">Calendar events. Only those that affect availability (or whose kind is in
    /// <see cref="EvaluationOptions.ExtraAvailabilityKinds"/>) can produce a vacation.</param>
    /// <param name="now">The instant to evaluate. Its offset is ignored; the instant is what counts.</param>
    /// <param name="options">Optional operator additions; null uses the defaults.</param>
    /// <returns>The state, its reason, and the next boundary strictly after <paramref name="now"/>.</returns>
    public static AvailabilityWindow Evaluate(
        AgentSchedule schedule,
        IReadOnlyList<CalendarEvent> events,
        DateTimeOffset now,
        EvaluationOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(schedule);
        ArgumentNullException.ThrowIfNull(events);

        var extraKinds = options?.ExtraAvailabilityKinds ?? ImmutableHashSet<string>.Empty;
        var today = LocalDate(now, schedule.Zone);
        var nextChange = FindNextChange(schedule, events, extraKinds, now, today);

        var vacation = FindVacation(events, extraKinds, now);
        if (vacation is not null)
        {
            return new AvailabilityWindow(AvailabilityState.Vacation, $"vacation:{vacation.Id}", nextChange);
        }

        if (IsInCurfew(schedule, now, today))
        {
            return new AvailabilityWindow(AvailabilityState.Off, "curfew", nextChange);
        }

        if (IsInShift(schedule, now, today))
        {
            return new AvailabilityWindow(AvailabilityState.OnShift, "shift", nextChange);
        }

        return new AvailabilityWindow(AvailabilityState.Off, "outside-shift", nextChange);
    }

    private static DateOnly LocalDate(DateTimeOffset instant, TimeZoneInfo zone) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(instant, zone).DateTime);

    private static bool IsAvailabilityEvent(CalendarEvent calendarEvent, IReadOnlySet<string> extraKinds) =>
        calendarEvent.AffectsAvailability || extraKinds.Contains(calendarEvent.Kind);

    private static CalendarEvent? FindVacation(
        IReadOnlyList<CalendarEvent> events,
        IReadOnlySet<string> extraKinds,
        DateTimeOffset now)
    {
        // With several covering events, the earliest start wins, then the smaller id, so the reason is stable.
        CalendarEvent? found = null;
        foreach (var calendarEvent in events)
        {
            if (!IsAvailabilityEvent(calendarEvent, extraKinds) || now < calendarEvent.Start || now >= calendarEvent.End)
            {
                continue;
            }

            if (found is null
                || calendarEvent.Start < found.Start
                || (calendarEvent.Start == found.Start && calendarEvent.Id.CompareTo(found.Id) < 0))
            {
                found = calendarEvent;
            }
        }

        return found;
    }

    private static bool IsInCurfew(AgentSchedule schedule, DateTimeOffset now, DateOnly today)
    {
        if (schedule.Curfew is not { } curfew)
        {
            return false;
        }

        for (var day = today.AddDays(-1); day <= today.AddDays(1); day = day.AddDays(1))
        {
            var (start, end) = WindowInstants(day, curfew.LocalStart, curfew.LocalEnd, schedule.Zone);
            if (start <= now && now < end)
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsInShift(AgentSchedule schedule, DateTimeOffset now, DateOnly today)
    {
        foreach (var shift in schedule.Shifts)
        {
            for (var day = today.AddDays(-1); day <= today.AddDays(1); day = day.AddDays(1))
            {
                if (!shift.Days.Contains(day.DayOfWeek))
                {
                    continue;
                }

                var (start, end) = WindowInstants(day, shift.LocalStart, shift.LocalEnd, schedule.Zone);
                if (start <= now && now < end)
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static DateTimeOffset? FindNextChange(
        AgentSchedule schedule,
        IReadOnlyList<CalendarEvent> events,
        IReadOnlySet<string> extraKinds,
        DateTimeOffset now,
        DateOnly today)
    {
        var horizonEnd = now + _horizon;
        var boundaries = new List<DateTimeOffset>();

        // Local days from the day before to nine days after today cover every window that can start or end in (now, now + 8 days].
        for (var day = today.AddDays(-1); day <= today.AddDays(9); day = day.AddDays(1))
        {
            foreach (var shift in schedule.Shifts)
            {
                if (shift.Days.Contains(day.DayOfWeek))
                {
                    AddBoundaries(boundaries, day, shift.LocalStart, shift.LocalEnd, schedule.Zone);
                }
            }

            if (schedule.Curfew is { } curfew)
            {
                AddBoundaries(boundaries, day, curfew.LocalStart, curfew.LocalEnd, schedule.Zone);
            }
        }

        foreach (var calendarEvent in events)
        {
            if (IsAvailabilityEvent(calendarEvent, extraKinds))
            {
                boundaries.Add(calendarEvent.Start);
                boundaries.Add(calendarEvent.End);
            }
        }

        return boundaries
            .Where(boundary => boundary > now && boundary <= horizonEnd)
            .Select(boundary => (DateTimeOffset?)boundary)
            .Min();
    }

    private static void AddBoundaries(
        List<DateTimeOffset> boundaries,
        DateOnly startDay,
        TimeOnly localStart,
        TimeOnly localEnd,
        TimeZoneInfo zone)
    {
        var (start, end) = WindowInstants(startDay, localStart, localEnd, zone);
        boundaries.Add(start);
        boundaries.Add(end);
    }

    /// <summary>
    /// The instants [start, end) of a local window that starts on <paramref name="startDay"/>. A window whose end
    /// is not after its start ends on the following day.
    /// </summary>
    private static (DateTimeOffset Start, DateTimeOffset End) WindowInstants(
        DateOnly startDay,
        TimeOnly localStart,
        TimeOnly localEnd,
        TimeZoneInfo zone)
    {
        var endDay = localEnd > localStart ? startDay : startDay.AddDays(1);
        return (
            ToInstant(startDay.ToDateTime(localStart), zone),
            ToInstant(endDay.ToDateTime(localEnd), zone));
    }

    /// <summary>
    /// Converts a local wall-clock time to an instant using the DST rules in the type remarks.
    /// </summary>
    private static DateTimeOffset ToInstant(DateTime local, TimeZoneInfo zone)
    {
        if (zone.IsInvalidTime(local))
        {
            // Gap: the offset before the change is in effect the day before at the same wall time.
            var offsetBefore = zone.GetUtcOffset(local.AddDays(-1));
            return new DateTimeOffset(local - offsetBefore, TimeSpan.Zero);
        }

        if (zone.IsAmbiguousTime(local))
        {
            // Overlap: the first occurrence is the earlier instant, which uses the larger offset.
            var earlierOffset = zone.GetAmbiguousTimeOffsets(local).Max();
            return new DateTimeOffset(local - earlierOffset, TimeSpan.Zero);
        }

        return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(local, zone), TimeSpan.Zero);
    }
}
