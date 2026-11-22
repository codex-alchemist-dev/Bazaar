namespace Bazaar.Client.Calendar.Tests;

public class CalendarDayMapperTests
{
    private static DateOnly D(int y, int m, int d) => new(y, m, d);

    [Fact]
    public void All_day_event_keeps_its_own_dates_in_every_zone()
    {
        var item = CalendarTestSupport.Event("a", "2026-12-24T00:00:00Z", "2026-12-26T00:00:00Z", allDay: true);

        foreach (var zone in new[] { CalendarTestSupport.NewYork, CalendarTestSupport.Tokyo, TimeZoneInfo.Utc })
        {
            var mapper = CalendarTestSupport.Mapper(zone);
            Assert.Equal((D(2026, 12, 24), D(2026, 12, 26)), mapper.DatesCovered(item, CalendarDisplayTimezone.Viewer));
            Assert.Equal((D(2026, 12, 24), D(2026, 12, 26)), mapper.DatesCovered(item, CalendarDisplayTimezone.Agent));
        }
    }

    [Fact]
    public void Timed_event_lands_on_the_viewers_local_date()
    {
        var item = CalendarTestSupport.Event("a", "2026-11-18T23:00:00Z", "2026-11-18T23:30:00Z");

        Assert.Equal((D(2026, 11, 19), D(2026, 11, 19)), CalendarTestSupport.Mapper(CalendarTestSupport.Tokyo).DatesCovered(item, CalendarDisplayTimezone.Viewer));
        Assert.Equal((D(2026, 11, 18), D(2026, 11, 18)), CalendarTestSupport.Mapper(CalendarTestSupport.NewYork).DatesCovered(item, CalendarDisplayTimezone.Viewer));
    }

    [Fact]
    public void Agent_mode_uses_the_event_zone_and_unknown_zone_falls_back_to_utc()
    {
        var resolver = new MapZoneResolver().Add("Asia/Tokyo", CalendarTestSupport.Tokyo);
        var mapper = CalendarTestSupport.Mapper(CalendarTestSupport.NewYork, resolver: resolver);
        var known = CalendarTestSupport.Event("a", "2026-11-18T23:00:00Z", "2026-11-18T23:30:00Z", tz: "Asia/Tokyo");
        var unknown = CalendarTestSupport.Event("b", "2026-11-18T23:00:00Z", "2026-11-18T23:30:00Z", tz: "Nowhere/Land");

        Assert.Equal(D(2026, 11, 19), mapper.DatesCovered(known, CalendarDisplayTimezone.Agent).First);
        Assert.Equal(D(2026, 11, 18), mapper.DatesCovered(unknown, CalendarDisplayTimezone.Agent).First);
        Assert.Equal(TimeZoneInfo.Utc, mapper.ZoneFor("Nowhere/Land"));
    }

    [Fact]
    public void System_resolver_returns_null_for_unknown_ids_without_throwing()
    {
        var resolver = new SystemCalendarZoneResolver();

        Assert.Null(resolver.Resolve("Not/AZone"));
        Assert.Null(resolver.Resolve(string.Empty));
        Assert.NotNull(resolver.Resolve("UTC"));
    }

    [Fact]
    public void Event_spanning_the_spring_forward_day_covers_each_date_once()
    {
        // 2026-03-08 is a 23 hour day in the test New York zone.
        var mapper = CalendarTestSupport.Mapper(CalendarTestSupport.NewYork, "2026-03-01T12:00:00Z");
        var item = CalendarTestSupport.Event("a", "2026-03-07T10:00:00Z", "2026-03-10T10:00:00Z");

        Assert.Equal((D(2026, 3, 7), D(2026, 3, 10)), mapper.DatesCovered(item, CalendarDisplayTimezone.Viewer));
    }

    [Fact]
    public void Event_ending_at_local_midnight_does_not_cover_the_next_day()
    {
        // 2026-11-01 is a 25 hour day. Local midnight 2026-11-02 is 05:00Z (standard time again).
        var mapper = CalendarTestSupport.Mapper(CalendarTestSupport.NewYork, "2026-11-01T12:00:00Z");
        var item = CalendarTestSupport.Event("a", "2026-11-01T04:00:00Z", "2026-11-02T05:00:00Z");

        Assert.Equal((D(2026, 11, 1), D(2026, 11, 1)), mapper.DatesCovered(item, CalendarDisplayTimezone.Viewer));
    }

    [Fact]
    public void Today_follows_the_viewer_zone()
    {
        Assert.Equal(D(2026, 11, 19), CalendarTestSupport.Mapper(CalendarTestSupport.Tokyo, "2026-11-18T20:00:00Z").Today);
        Assert.Equal(D(2026, 11, 18), CalendarTestSupport.Mapper(CalendarTestSupport.NewYork, "2026-11-18T20:00:00Z").Today);
    }
}
