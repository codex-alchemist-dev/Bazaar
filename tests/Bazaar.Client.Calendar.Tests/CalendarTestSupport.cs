using System.Text.Json;
using Avalonia;
using Avalonia.Headless;
using Bazaar.Client.Calendar;
using Bazaar.Client.Core;
using Bazaar.Contracts.Panes;

[assembly: AvaloniaTestApplication(typeof(Bazaar.Client.Calendar.Tests.TestAppBuilder))]

namespace Bazaar.Client.Calendar.Tests;

/// <summary>Builds the Avalonia application for headless tests: no display server is needed.</summary>
public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}

/// <summary>A clock fixed at one instant with a fixed viewer zone.</summary>
internal sealed class FixedTimeProvider : TimeProvider
{
    private readonly DateTimeOffset _now;
    private readonly TimeZoneInfo _zone;

    public FixedTimeProvider(DateTimeOffset now, TimeZoneInfo zone)
    {
        _now = now;
        _zone = zone;
    }

    public override TimeZoneInfo LocalTimeZone => _zone;

    public override DateTimeOffset GetUtcNow() => _now;
}

internal sealed class MapZoneResolver : ICalendarZoneResolver
{
    private readonly Dictionary<string, TimeZoneInfo> _zones = [];

    public MapZoneResolver Add(string id, TimeZoneInfo zone)
    {
        _zones[id] = zone;
        return this;
    }

    public TimeZoneInfo? Resolve(string zoneId) => _zones.GetValueOrDefault(zoneId);
}

internal static class CalendarTestSupport
{
    /// <summary>A zone with US-style DST: UTC-5 standard, UTC-4 from 2026-03-08 02:00 to 2026-11-01 02:00.</summary>
    public static TimeZoneInfo NewYork { get; } = BuildZone("Test/NewYork", TimeSpan.FromHours(-5), 3, 8, 11, 1);

    /// <summary>A zone ahead of UTC with no DST.</summary>
    public static TimeZoneInfo Tokyo { get; } = TimeZoneInfo.CreateCustomTimeZone("Test/Tokyo", TimeSpan.FromHours(9), "Tokyo", "Tokyo");

    public static CalendarDayMapper Mapper(TimeZoneInfo viewer, string utcNow = "2026-11-18T12:00:00Z", ICalendarZoneResolver? resolver = null) =>
        new(new FixedTimeProvider(DateTimeOffset.Parse(utcNow, System.Globalization.CultureInfo.InvariantCulture), viewer), resolver ?? new MapZoneResolver());

    public static PaneDescriptor Descriptor(string modelJson)
    {
        return new PaneDescriptor("bazaar.calendar", "Calendar", string.Empty, JsonElement.Parse(modelJson));
    }

    public static CalendarEventSummary Event(
        string id,
        string startsAt,
        string endsAt,
        bool allDay = false,
        string tz = "UTC",
        CalendarEventStatus status = CalendarEventStatus.Confirmed,
        CalendarEventKind kind = CalendarEventKind.Release) =>
        new(
            id,
            kind,
            "title " + id,
            DateTimeOffset.Parse(startsAt, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal),
            DateTimeOffset.Parse(endsAt, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal),
            allDay,
            tz,
            null,
            null,
            false,
            status);

    private static TimeZoneInfo BuildZone(string id, TimeSpan standard, int dstStartMonth, int dstStartDay, int dstEndMonth, int dstEndDay)
    {
        var start = TimeZoneInfo.TransitionTime.CreateFixedDateRule(new DateTime(1, 1, 1, 2, 0, 0), dstStartMonth, dstStartDay);
        var end = TimeZoneInfo.TransitionTime.CreateFixedDateRule(new DateTime(1, 1, 1, 2, 0, 0), dstEndMonth, dstEndDay);
        var rule = TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(DateTime.MinValue.Date, DateTime.MaxValue.Date, TimeSpan.FromHours(1), start, end);
        return TimeZoneInfo.CreateCustomTimeZone(id, standard, id, id, id, [rule]);
    }
}
