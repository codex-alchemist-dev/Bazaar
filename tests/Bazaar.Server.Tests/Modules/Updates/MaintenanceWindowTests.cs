using System.Globalization;
using Bazaar.Server.Modules.Updates;

namespace Bazaar.Server.Tests.Modules.Updates;

/// <summary>Maintenance window membership, next start and validation (doc 42 section 10.1).</summary>
public sealed class MaintenanceWindowTests
{
    private static MaintenanceWindow Window(DayOfWeek[] days, string start, string end, MaintenanceZone? zone = null) =>
        new(days, TimeOnly.Parse(start, CultureInfo.InvariantCulture), TimeOnly.Parse(end, CultureInfo.InvariantCulture), zone ?? MaintenanceZone.Fixed(TimeSpan.Zero));

    private static MaintenanceZone Berlin() => MaintenanceZone.Named(TimeZoneInfo.FindSystemTimeZoneById("Europe/Berlin"));

    [Theory]
    [InlineData("2026-11-16T09:59:59Z", false)]
    [InlineData("2026-11-16T10:00:00Z", true)]
    [InlineData("2026-11-16T11:59:59Z", true)]
    [InlineData("2026-11-16T12:00:00Z", false)]
    public void Contains_treats_start_as_inclusive_and_end_as_exclusive(string instant, bool expected)
    {
        var window = Window([DayOfWeek.Monday], "10:00", "12:00");

        Assert.Equal(expected, window.Contains(DateTimeOffset.Parse(instant, CultureInfo.InvariantCulture)));
    }

    [Fact]
    public void Contains_is_false_on_a_day_the_window_does_not_list()
    {
        var window = Window([DayOfWeek.Saturday], "10:00", "12:00");

        Assert.False(window.Contains(DateTimeOffset.Parse("2026-11-16T11:00:00Z", CultureInfo.InvariantCulture)));
        Assert.True(window.Contains(DateTimeOffset.Parse("2026-11-21T11:00:00Z", CultureInfo.InvariantCulture)));
    }

    [Fact]
    public void Contains_reads_the_clock_of_a_named_zone()
    {
        // Berlin is on CET (UTC+1) in mid November: 02:00 local is 01:00 UTC and 04:00 local is 03:00 UTC.
        var window = Window([DayOfWeek.Monday], "02:00", "04:00", Berlin());

        Assert.False(window.Contains(DateTimeOffset.Parse("2026-11-16T00:59:00Z", CultureInfo.InvariantCulture)));
        Assert.True(window.Contains(DateTimeOffset.Parse("2026-11-16T01:00:00Z", CultureInfo.InvariantCulture)));
        Assert.False(window.Contains(DateTimeOffset.Parse("2026-11-16T03:00:00Z", CultureInfo.InvariantCulture)));
    }

    [Fact]
    public void Contains_reads_a_fixed_offset_zone()
    {
        // A fixed +02:00 window on Monday 10:00-12:00 local is 08:00-10:00 UTC.
        var window = Window([DayOfWeek.Monday], "10:00", "12:00", MaintenanceZone.Fixed(TimeSpan.FromHours(2)));

        Assert.True(window.Contains(DateTimeOffset.Parse("2026-11-16T08:00:00Z", CultureInfo.InvariantCulture)));
        Assert.False(window.Contains(DateTimeOffset.Parse("2026-11-16T10:00:00Z", CultureInfo.InvariantCulture)));
    }

    [Fact]
    public void Next_start_is_later_the_same_day_when_the_window_has_not_begun()
    {
        var window = Window([DayOfWeek.Monday], "10:00", "12:00");

        var next = window.NextStartAfter(DateTimeOffset.Parse("2026-11-16T09:00:00Z", CultureInfo.InvariantCulture));

        Assert.Equal(DateTimeOffset.Parse("2026-11-16T10:00:00Z", CultureInfo.InvariantCulture), next);
    }

    [Fact]
    public void Next_start_rolls_over_to_next_week_once_the_slot_has_begun()
    {
        var window = Window([DayOfWeek.Friday], "10:00", "12:00");

        var next = window.NextStartAfter(DateTimeOffset.Parse("2026-11-20T11:00:00Z", CultureInfo.InvariantCulture));

        Assert.Equal(DateTimeOffset.Parse("2026-11-27T10:00:00Z", CultureInfo.InvariantCulture), next);
    }

    [Fact]
    public void Next_start_across_a_set_is_the_earliest_of_its_windows()
    {
        var windows = new[]
        {
            Window([DayOfWeek.Thursday], "10:00", "12:00"),
            Window([DayOfWeek.Wednesday], "22:00", "23:00"),
        };

        var next = MaintenanceWindowValidator.NextStartAfter(windows, DateTimeOffset.Parse("2026-11-16T12:00:00Z", CultureInfo.InvariantCulture));

        Assert.Equal(DateTimeOffset.Parse("2026-11-18T22:00:00Z", CultureInfo.InvariantCulture), next);
    }

    [Fact]
    public void Next_start_of_an_empty_set_is_null()
    {
        Assert.Null(MaintenanceWindowValidator.NextStartAfter([], DateTimeOffset.UtcNow));
    }

    [Fact]
    public void Valid_set_passes_validation()
    {
        var windows = new[]
        {
            Window([DayOfWeek.Monday, DayOfWeek.Wednesday], "10:00", "12:00"),
            Window([DayOfWeek.Tuesday], "10:00", "12:00", Berlin()),
        };

        Assert.True(MaintenanceWindowValidator.Validate(windows).IsValid);
    }

    [Fact]
    public void Adjacent_windows_on_the_same_day_do_not_overlap()
    {
        var windows = new[]
        {
            Window([DayOfWeek.Monday], "10:00", "11:00"),
            Window([DayOfWeek.Monday], "11:00", "12:00"),
        };

        Assert.True(MaintenanceWindowValidator.Validate(windows).IsValid);
    }

    [Fact]
    public void Windows_on_different_days_at_the_same_time_do_not_overlap()
    {
        var windows = new[]
        {
            Window([DayOfWeek.Monday], "10:00", "12:00"),
            Window([DayOfWeek.Tuesday], "10:00", "12:00"),
        };

        Assert.True(MaintenanceWindowValidator.Validate(windows).IsValid);
    }

    [Fact]
    public void Window_with_no_days_is_rejected()
    {
        var validation = MaintenanceWindowValidator.Validate([Window([], "10:00", "12:00")]);

        var problem = Assert.Single(validation.Problems);
        Assert.Equal(0, problem.WindowIndex);
        Assert.Equal(MaintenanceWindowIssue.NoDays, problem.Issue);
    }

    [Fact]
    public void Empty_range_is_rejected()
    {
        var validation = MaintenanceWindowValidator.Validate([Window([DayOfWeek.Monday], "10:00", "10:00")]);

        Assert.Equal(MaintenanceWindowIssue.EmptyRange, Assert.Single(validation.Problems).Issue);
    }

    [Fact]
    public void Inverted_range_is_rejected()
    {
        var validation = MaintenanceWindowValidator.Validate([Window([DayOfWeek.Monday], "12:00", "10:00")]);

        Assert.Equal(MaintenanceWindowIssue.InvertedRange, Assert.Single(validation.Problems).Issue);
    }

    [Fact]
    public void Fixed_offset_beyond_fourteen_hours_is_rejected()
    {
        var validation = MaintenanceWindowValidator.Validate([Window([DayOfWeek.Monday], "10:00", "12:00", MaintenanceZone.Fixed(TimeSpan.FromHours(15)))]);

        Assert.Equal(MaintenanceWindowIssue.InvalidZone, Assert.Single(validation.Problems).Issue);
    }

    [Fact]
    public void Missing_named_zone_is_rejected()
    {
        var validation = MaintenanceWindowValidator.Validate([Window([DayOfWeek.Monday], "10:00", "12:00", MaintenanceZone.Named(null))]);

        Assert.Equal(MaintenanceWindowIssue.InvalidZone, Assert.Single(validation.Problems).Issue);
    }

    [Fact]
    public void Overlapping_windows_in_one_zone_are_rejected()
    {
        var validation = MaintenanceWindowValidator.Validate(
        [
            Window([DayOfWeek.Monday], "10:00", "12:00"),
            Window([DayOfWeek.Monday], "11:00", "13:00"),
        ]);

        var problem = Assert.Single(validation.Problems);
        Assert.Equal(MaintenanceWindowIssue.Overlap, problem.Issue);
        Assert.Equal(1, problem.WindowIndex);
        Assert.Equal(0, problem.OtherWindowIndex);
    }

    [Fact]
    public void Windows_that_overlap_across_zones_are_rejected()
    {
        // Berlin 09:00-10:00 on Monday is 08:00-09:00 UTC, which overlaps 08:30-09:30 UTC.
        var validation = MaintenanceWindowValidator.Validate(
        [
            Window([DayOfWeek.Monday], "09:00", "10:00", Berlin()),
            Window([DayOfWeek.Monday], "08:30", "09:30"),
        ]);

        Assert.Equal(MaintenanceWindowIssue.Overlap, Assert.Single(validation.Problems).Issue);
    }

    [Fact]
    public void Validation_reports_every_problem_not_only_the_first()
    {
        var validation = MaintenanceWindowValidator.Validate(
        [
            Window([], "10:00", "12:00"),
            Window([DayOfWeek.Monday], "12:00", "10:00"),
            Window([DayOfWeek.Monday], "10:00", "10:00", MaintenanceZone.Fixed(TimeSpan.FromHours(20))),
        ]);

        Assert.False(validation.IsValid);
        Assert.Equal(
            [MaintenanceWindowIssue.NoDays, MaintenanceWindowIssue.InvertedRange, MaintenanceWindowIssue.EmptyRange, MaintenanceWindowIssue.InvalidZone],
            validation.Problems.Select(p => p.Issue));
        Assert.Equal([0, 1, 2, 2], validation.Problems.Select(p => p.WindowIndex));
    }
}
