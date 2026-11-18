using Bazaar.Client.Calendar.Panes;
using Bazaar.Client.Core.Panes;

namespace Bazaar.Client.Calendar.Tests;

public class CalendarPaneTests
{
    private static string Model(string events, string mode = "viewer") => $$"""{"displayTimezone":"{{mode}}","events":[{{events}}]}""";

    private static string Item(
        string id = "e1",
        string kind = "release",
        string status = "confirmed",
        string startsAt = "2026-11-10T09:00:00Z",
        string endsAt = "2026-11-10T10:00:00Z",
        bool allDay = false) =>
        $$"""{"id":"{{id}}","kind":"{{kind}}","title":"T {{id}}","startsAt":"{{startsAt}}","endsAt":"{{endsAt}}","allDay":{{(allDay ? "true" : "false")}},"tz":"UTC","agentId":null,"projectId":null,"affectsAvailability":false,"status":"{{status}}"}""";

    private static CalendarPaneViewModel Pane(string model) =>
        new(CalendarTestSupport.Descriptor(model), CalendarTestSupport.Mapper(TimeZoneInfo.Utc));

    [Theory]
    [InlineData("""{"displayTimezone":"viewer"}""")]
    [InlineData("""{"displayTimezone":"moon","events":[]}""")]
    [InlineData("""{"events":[]}""")]
    public void Malformed_top_level_raises_pane_model_exception(string model) =>
        Assert.Throws<PaneModelException>(() => Pane(model));

    [Fact]
    public void Bad_kind_status_timestamp_or_inverted_range_raise()
    {
        Assert.Throws<PaneModelException>(() => Pane(Model(Item(kind: "party"))));
        Assert.Throws<PaneModelException>(() => Pane(Model(Item(status: "maybe"))));
        Assert.Throws<PaneModelException>(() => Pane(Model(Item(startsAt: "yesterday-ish"))));
        Assert.Throws<PaneModelException>(() => Pane(Model(Item(startsAt: "2026-11-10T10:00:00Z", endsAt: "2026-11-10T09:00:00Z"))));
    }

    [Fact]
    public void Month_grid_starts_on_the_current_month_with_monday_first_weeks()
    {
        var pane = Pane(Model(string.Empty));

        Assert.Equal("November 2026", pane.MonthLabel);
        var weeks = pane.BuildWeeks();
        Assert.Equal(new DateOnly(2026, 10, 26), weeks[0][0].Date);
        Assert.Equal(new DateOnly(2026, 12, 6), weeks[^1][6].Date);
        Assert.All(weeks, w => Assert.Equal(7, w.Count));
        Assert.Single(weeks.SelectMany(w => w), d => d.IsToday);
    }

    [Fact]
    public void Multi_day_event_has_one_chip_per_day_it_covers()
    {
        var pane = Pane(Model(Item(kind: "vacation", startsAt: "2026-11-10", endsAt: "2026-11-12", allDay: true)));

        var days = pane.BuildWeeks().SelectMany(w => w).Where(d => d.Chips.Count > 0).Select(d => d.Date).ToArray();

        Assert.Equal([new DateOnly(2026, 11, 10), new DateOnly(2026, 11, 11), new DateOnly(2026, 11, 12)], days);
    }

    [Fact]
    public void Cancelled_events_stay_on_the_grid_and_say_so_in_text()
    {
        var pane = Pane(Model(Item(status: "cancelled", kind: "freeze")));

        var chip = pane.BuildWeeks().SelectMany(w => w).SelectMany(d => d.Chips).Single();

        Assert.True(chip.IsCancelled);
        Assert.Contains("Freeze", chip.AccessibleName, StringComparison.Ordinal);
        Assert.Contains("Cancelled", chip.AccessibleName, StringComparison.Ordinal);
        Assert.Contains("(cancelled)", chip.Label, StringComparison.Ordinal);
    }

    [Fact]
    public void Month_navigation_moves_and_returns_to_today()
    {
        var pane = Pane(Model(Item()));

        pane.NextMonth();
        Assert.Equal("December 2026", pane.MonthLabel);
        Assert.Empty(pane.BuildWeeks().SelectMany(w => w).SelectMany(d => d.Chips));
        pane.PreviousMonth();
        pane.PreviousMonth();
        Assert.Equal("October 2026", pane.MonthLabel);
        pane.GoToToday();
        Assert.Equal("November 2026", pane.MonthLabel);
    }

    [Fact]
    public void Dst_boundary_month_keeps_every_day_exactly_once()
    {
        var mapper = CalendarTestSupport.Mapper(CalendarTestSupport.NewYork, "2026-11-18T12:00:00Z");
        var pane = new CalendarPaneViewModel(CalendarTestSupport.Descriptor(Model(string.Empty)), mapper);

        var dates = pane.BuildWeeks().SelectMany(w => w).Select(d => d.Date).ToArray();

        Assert.Equal(dates.Length, dates.Distinct().Count());
        Assert.Contains(new DateOnly(2026, 11, 1), dates);
        Assert.Contains(new DateOnly(2026, 11, 2), dates);
    }

    [Fact]
    public void Module_registers_the_pane_kind()
    {
        var registry = PaneRegistry.Compose(CalendarPanes.Modules);

        Assert.True(registry.Contains("bazaar.calendar"));
    }
}
