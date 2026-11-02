// TODO(component): move to the Company component project when PR #125 merges.
using Bazaar.Server.Calendar;
using Bazaar.Server.WorkLife;

namespace Bazaar.Server.Company.Availability;

/// <summary>A calendar row that was left out of the availability input, with the reason.</summary>
/// <param name="Id">The calendar event id.</param>
/// <param name="Reason">A short plain reason, for logs and the audit trail.</param>
public sealed record CalendarAvailabilitySkip(Guid Id, string Reason);

/// <summary>The availability input for one agent, and the rows that could not be used.</summary>
/// <param name="Events">Events for <see cref="ScheduleEvaluator.Evaluate"/>, in input order.</param>
/// <param name="Skipped">Rows that were dropped because they could not be turned into an event.</param>
public sealed record CalendarAvailabilityResult(
    IReadOnlyList<CalendarEvent> Events,
    IReadOnlyList<CalendarAvailabilitySkip> Skipped);

/// <summary>
/// Turns stored Company Calendar rows into the events that the pure availability evaluator reads (doc 24 WP8, doc 09).
/// The calendar writes no availability; this only reads. It does no I/O and writes no state.
/// </summary>
/// <remarks>
/// Only confirmed rows block availability. A row is used when it belongs to the agent, or when it is a company-wide
/// vacation that affects availability. Timed rows use their stored instants. All-day rows are local dates in their own
/// zone: they run from local midnight of the first day to local midnight of the day after the last day.
/// </remarks>
public static class CalendarAvailabilityFeed
{
    /// <summary>Converts the rows that affect <paramref name="agentId"/>'s availability.</summary>
    /// <param name="records">Stored calendar rows, in any order.</param>
    /// <param name="agentId">The agent being evaluated.</param>
    /// <param name="options">Operator additions; their kinds also count, for the agent's own rows.</param>
    public static CalendarAvailabilityResult ToAvailabilityEvents(
        IEnumerable<CalendarEventRecord> records,
        Guid agentId,
        EvaluationOptions options)
    {
        ArgumentNullException.ThrowIfNull(records);
        ArgumentNullException.ThrowIfNull(options);

        var events = new List<CalendarEvent>();
        var skipped = new List<CalendarAvailabilitySkip>();

        foreach (var record in records)
        {
            if (!IsRelevant(record, agentId, options))
            {
                continue;
            }

            if (record.EndsAt <= record.StartsAt)
            {
                skipped.Add(new CalendarAvailabilitySkip(record.Id, "inverted or empty range"));
                continue;
            }

            var kind = KindText(record.Kind);
            if (!record.AllDay)
            {
                events.Add(new CalendarEvent(record.Id, kind, record.StartsAt, record.EndsAt, record.AffectsAvailability));
                continue;
            }

            if (!TryResolveZone(record.Tz, out var zone))
            {
                skipped.Add(new CalendarAvailabilitySkip(record.Id, "unknown time zone"));
                continue;
            }

            var firstDay = LocalDate(record.StartsAt, zone);
            // EndsAt is exclusive; the last local day is the day that holds the instant just before it.
            var lastDay = LocalDate(record.EndsAt.AddTicks(-1), zone);
            var start = LocalMidnight(firstDay, zone);
            var end = LocalMidnight(lastDay.AddDays(1), zone);
            if (end <= start)
            {
                skipped.Add(new CalendarAvailabilitySkip(record.Id, "inverted or empty range"));
                continue;
            }

            events.Add(new CalendarEvent(record.Id, kind, start, end, record.AffectsAvailability));
        }

        return new CalendarAvailabilityResult(events, skipped);
    }

    private static bool IsRelevant(CalendarEventRecord record, Guid agentId, EvaluationOptions options)
    {
        if (record.Status != CalendarStatus.Confirmed)
        {
            return false;
        }

        if (record.AgentId is null)
        {
            // Company-wide rows only count as vacation that affects availability.
            return record.AffectsAvailability && record.Kind == CalendarKind.Vacation;
        }

        return record.AgentId == agentId
            && (record.AffectsAvailability || options.ExtraAvailabilityKinds.Contains(KindText(record.Kind)));
    }

    private static string KindText(CalendarKind kind) => kind switch
    {
        CalendarKind.Vacation => "vacation",
        CalendarKind.Planning => "planning",
        CalendarKind.Release => "release",
        CalendarKind.Freeze => "freeze",
        CalendarKind.Milestone => "milestone",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown calendar kind."),
    };

    private static bool TryResolveZone(string tz, out TimeZoneInfo zone)
    {
        try
        {
            zone = TimeZoneInfo.FindSystemTimeZoneById(tz);
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            zone = TimeZoneInfo.Utc;
            return false;
        }
    }

    private static DateOnly LocalDate(DateTimeOffset instant, TimeZoneInfo zone) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(instant, zone).DateTime);

    /// <summary>
    /// The instant of local midnight. When midnight does not exist (a gap) or is repeated (an overlap), the offset in effect
    /// before the change is used, the same rule <see cref="ScheduleEvaluator"/> follows.
    /// </summary>
    private static DateTimeOffset LocalMidnight(DateOnly day, TimeZoneInfo zone)
    {
        var local = day.ToDateTime(TimeOnly.MinValue);
        var offset = zone.IsInvalidTime(local) || zone.IsAmbiguousTime(local)
            ? zone.GetUtcOffset(local.AddHours(-1))
            : zone.GetUtcOffset(local);
        return new DateTimeOffset(local, offset);
    }
}
