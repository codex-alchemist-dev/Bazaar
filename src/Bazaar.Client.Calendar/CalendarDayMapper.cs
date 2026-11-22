namespace Bazaar.Client.Calendar;

/// <summary>Resolves a zone id to a zone, or null when the client does not know it.</summary>
public interface ICalendarZoneResolver
{
    TimeZoneInfo? Resolve(string zoneId);
}

/// <summary>Looks zone ids up with <see cref="TimeZoneInfo.FindSystemTimeZoneById(string)"/>.</summary>
public sealed class SystemCalendarZoneResolver : ICalendarZoneResolver
{
    public TimeZoneInfo? Resolve(string zoneId)
    {
        if (string.IsNullOrWhiteSpace(zoneId))
        {
            return null;
        }

        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(zoneId);
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException or ArgumentException)
        {
            return null;
        }
    }
}

/// <summary>
/// Pure time-zone logic of the calendar: which calendar dates an event covers. All-day events keep the date they were
/// written with, in every zone. Timed events land on the date in the viewer's zone (or the event's zone in agent mode).
/// An unknown zone id falls back to UTC. Only date arithmetic builds the grid, so 23 and 25 hour days cannot shift it.
/// </summary>
public sealed class CalendarDayMapper
{
    private readonly TimeProvider _time;
    private readonly ICalendarZoneResolver _resolver;

    public CalendarDayMapper(TimeProvider? time = null, ICalendarZoneResolver? resolver = null)
    {
        _time = time ?? TimeProvider.System;
        _resolver = resolver ?? new SystemCalendarZoneResolver();
    }

    /// <summary>The viewer's zone, from the injected time provider.</summary>
    public TimeZoneInfo ViewerZone => _time.LocalTimeZone;

    /// <summary>Today's date in the viewer's zone.</summary>
    public DateOnly Today => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(_time.GetUtcNow(), ViewerZone).DateTime);

    /// <summary>The zone for an id; UTC when the id is empty or unknown.</summary>
    public TimeZoneInfo ZoneFor(string zoneId) => _resolver.Resolve(zoneId) ?? TimeZoneInfo.Utc;

    /// <summary>
    /// First and last calendar date (inclusive) the event covers. An all-day event spans the dates it was written with.
    /// A timed event ending exactly at local midnight does not cover the next day.
    /// </summary>
    public (DateOnly First, DateOnly Last) DatesCovered(CalendarEventSummary item, CalendarDisplayTimezone mode)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (item.AllDay)
        {
            return (DateOnly.FromDateTime(item.StartsAt.DateTime), DateOnly.FromDateTime(item.EndsAt.DateTime));
        }

        var zone = mode == CalendarDisplayTimezone.Agent ? ZoneFor(item.Tz) : ViewerZone;
        var start = TimeZoneInfo.ConvertTime(item.StartsAt, zone);
        var end = TimeZoneInfo.ConvertTime(item.EndsAt, zone);
        var first = DateOnly.FromDateTime(start.DateTime);
        var last = DateOnly.FromDateTime(end.DateTime);
        if (end > start && end.TimeOfDay == TimeSpan.Zero && last > first)
        {
            last = last.AddDays(-1);
        }

        return (first, last);
    }
}
