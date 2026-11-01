using System.Globalization;
using Bazaar.Server.Calendar;
using Bazaar.Server.Company.Availability;
using Bazaar.Server.WorkLife;

namespace Bazaar.Server.Tests.Company.Availability;

public class CalendarAvailabilityFeedTests
{
    private static readonly Guid _agentId = Guid.Parse("0196a6f0-0000-7000-8000-000000000301");
    private static readonly Guid _otherAgentId = Guid.Parse("0196a6f0-0000-7000-8000-000000000302");
    private static readonly Guid _vacationId = Guid.Parse("0196a6f0-0000-7000-8000-000000000311");
    private static readonly Guid _otherRowId = Guid.Parse("0196a6f0-0000-7000-8000-000000000312");
    private static readonly Guid _proposedId = Guid.Parse("0196a6f0-0000-7000-8000-000000000313");
    private static readonly Guid _cancelledId = Guid.Parse("0196a6f0-0000-7000-8000-000000000314");
    private static readonly Guid _planningId = Guid.Parse("0196a6f0-0000-7000-8000-000000000315");

    private static readonly EvaluationOptions _defaults = new();

    private static DateTimeOffset At(string iso) =>
        DateTimeOffset.Parse(iso, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);

    private static CalendarEventRecord Row(
        Guid id,
        CalendarKind kind,
        string startsAt,
        string endsAt,
        Guid? agentId = null,
        bool affects = true,
        CalendarStatus status = CalendarStatus.Confirmed,
        bool allDay = false,
        string tz = "UTC") => new()
        {
            Id = id,
            Kind = kind,
            TitleKeyId = Guid.Empty,
            TitleCiphertext = [],
            StartsAt = At(startsAt),
            EndsAt = At(endsAt),
            AllDay = allDay,
            Tz = tz,
            AgentId = agentId,
            AffectsAvailability = affects,
            Status = status,
            Version = 1,
        };

    private static void RequireZone(string id)
    {
        try
        {
            TimeZoneInfo.FindSystemTimeZoneById(id);
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            Assert.Skip($"time zone '{id}' is not installed on this machine");
        }
    }

    [Fact]
    public void Other_agents_rows_are_ignored()
    {
        var records = new[]
        {
            Row(_otherRowId, CalendarKind.Vacation, "2026-06-15T09:00:00Z", "2026-06-16T09:00:00Z", agentId: _otherAgentId),
        };

        var result = CalendarAvailabilityFeed.ToAvailabilityEvents(records, _agentId, _defaults);

        Assert.Empty(result.Events);
        Assert.Empty(result.Skipped);
    }

    [Theory]
    [InlineData(CalendarStatus.Proposed)]
    [InlineData(CalendarStatus.Cancelled)]
    public void Proposed_and_cancelled_rows_are_ignored(CalendarStatus status)
    {
        var records = new[]
        {
            Row(_proposedId, CalendarKind.Vacation, "2026-06-15T09:00:00Z", "2026-06-16T09:00:00Z", agentId: _agentId, status: status),
        };

        var result = CalendarAvailabilityFeed.ToAvailabilityEvents(records, _agentId, _defaults);

        Assert.Empty(result.Events);
        Assert.Empty(result.Skipped);
    }

    [Fact]
    public void Confirmed_vacation_is_kept_with_its_stored_instants()
    {
        var records = new[]
        {
            Row(_vacationId, CalendarKind.Vacation, "2026-06-15T09:00:00Z", "2026-06-16T09:00:00Z", agentId: _agentId),
        };

        var result = CalendarAvailabilityFeed.ToAvailabilityEvents(records, _agentId, _defaults);

        var calendarEvent = Assert.Single(result.Events);
        Assert.Equal(_vacationId, calendarEvent.Id);
        Assert.Equal("vacation", calendarEvent.Kind);
        Assert.Equal(At("2026-06-15T09:00:00Z"), calendarEvent.Start);
        Assert.Equal(At("2026-06-16T09:00:00Z"), calendarEvent.End);
        Assert.True(calendarEvent.AffectsAvailability);
        Assert.Empty(result.Skipped);
    }

    [Fact]
    public void Company_wide_vacation_that_affects_availability_is_kept()
    {
        var records = new[]
        {
            Row(_vacationId, CalendarKind.Vacation, "2026-06-15T09:00:00Z", "2026-06-16T09:00:00Z"),
            Row(_planningId, CalendarKind.Planning, "2026-06-15T09:00:00Z", "2026-06-16T09:00:00Z"),
        };

        var result = CalendarAvailabilityFeed.ToAvailabilityEvents(records, _agentId, _defaults);

        var calendarEvent = Assert.Single(result.Events);
        Assert.Equal(_vacationId, calendarEvent.Id);
    }

    [Fact]
    public void Extra_kinds_from_options_count_for_the_agents_own_rows()
    {
        var options = new EvaluationOptions { ExtraAvailabilityKinds = new HashSet<string> { "planning" } };
        var records = new[]
        {
            Row(_planningId, CalendarKind.Planning, "2026-06-15T09:00:00Z", "2026-06-16T09:00:00Z", agentId: _agentId, affects: false),
        };

        var result = CalendarAvailabilityFeed.ToAvailabilityEvents(records, _agentId, options);

        var calendarEvent = Assert.Single(result.Events);
        Assert.Equal("planning", calendarEvent.Kind);
        Assert.False(calendarEvent.AffectsAvailability);
    }

    [Fact]
    public void Row_that_does_not_affect_availability_and_has_no_extra_kind_is_dropped()
    {
        var records = new[]
        {
            Row(_planningId, CalendarKind.Planning, "2026-06-15T09:00:00Z", "2026-06-16T09:00:00Z", agentId: _agentId, affects: false),
        };

        var result = CalendarAvailabilityFeed.ToAvailabilityEvents(records, _agentId, _defaults);

        Assert.Empty(result.Events);
        Assert.Empty(result.Skipped);
    }

    [Fact]
    public void All_day_rows_span_local_days_in_each_rows_own_zone()
    {
        var paris = Row(
            _vacationId, CalendarKind.Vacation,
            "2026-06-15T00:00:00+02:00", "2026-06-17T00:00:00+02:00",
            agentId: _agentId, allDay: true, tz: "Europe/Paris");
        var tokyo = Row(
            _otherRowId, CalendarKind.Vacation,
            "2026-06-15T00:00:00+09:00", "2026-06-16T00:00:00+09:00",
            agentId: _agentId, allDay: true, tz: "Asia/Tokyo");
        RequireZone("Europe/Paris");
        RequireZone("Asia/Tokyo");

        var result = CalendarAvailabilityFeed.ToAvailabilityEvents(new[] { paris, tokyo }, _agentId, _defaults);

        Assert.Empty(result.Skipped);
        Assert.Equal(2, result.Events.Count);
        // Paris: 15 June 00:00 CEST to 17 June 00:00 CEST (two local days).
        Assert.Equal(At("2026-06-14T22:00:00Z"), result.Events[0].Start);
        Assert.Equal(At("2026-06-16T22:00:00Z"), result.Events[0].End);
        // Tokyo: one local day, 15 June 00:00 JST to 16 June 00:00 JST.
        Assert.Equal(At("2026-06-14T15:00:00Z"), result.Events[1].Start);
        Assert.Equal(At("2026-06-15T15:00:00Z"), result.Events[1].End);
    }

    [Fact]
    public void All_day_row_on_the_New_York_spring_forward_day_is_23_hours()
    {
        RequireZone("America/New_York");
        var row = Row(
            _vacationId, CalendarKind.Vacation,
            "2026-03-08T00:00:00-05:00", "2026-03-09T00:00:00-04:00",
            agentId: _agentId, allDay: true, tz: "America/New_York");

        var result = CalendarAvailabilityFeed.ToAvailabilityEvents(new[] { row }, _agentId, _defaults);

        var calendarEvent = Assert.Single(result.Events);
        Assert.Equal(At("2026-03-08T05:00:00Z"), calendarEvent.Start);
        Assert.Equal(At("2026-03-09T04:00:00Z"), calendarEvent.End);
        Assert.Equal(TimeSpan.FromHours(23), calendarEvent.End - calendarEvent.Start);
    }

    [Fact]
    public void Inverted_timed_range_is_skipped_and_reported()
    {
        var row = Row(_vacationId, CalendarKind.Vacation, "2026-06-16T09:00:00Z", "2026-06-15T09:00:00Z", agentId: _agentId);

        var result = CalendarAvailabilityFeed.ToAvailabilityEvents(new[] { row }, _agentId, _defaults);

        Assert.Empty(result.Events);
        var skip = Assert.Single(result.Skipped);
        Assert.Equal(_vacationId, skip.Id);
        Assert.Contains("range", skip.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Equal_timed_range_is_skipped_and_reported()
    {
        var row = Row(_vacationId, CalendarKind.Vacation, "2026-06-15T09:00:00Z", "2026-06-15T09:00:00Z", agentId: _agentId);

        var result = CalendarAvailabilityFeed.ToAvailabilityEvents(new[] { row }, _agentId, _defaults);

        Assert.Empty(result.Events);
        Assert.Equal(_vacationId, Assert.Single(result.Skipped).Id);
    }

    [Fact]
    public void All_day_row_with_an_unknown_zone_is_skipped_and_reported()
    {
        var row = Row(
            _vacationId, CalendarKind.Vacation,
            "2026-06-15T00:00:00Z", "2026-06-16T00:00:00Z",
            agentId: _agentId, allDay: true, tz: "Not/AZone");

        var result = CalendarAvailabilityFeed.ToAvailabilityEvents(new[] { row }, _agentId, _defaults);

        Assert.Empty(result.Events);
        var skip = Assert.Single(result.Skipped);
        Assert.Equal(_vacationId, skip.Id);
        Assert.Contains("zone", skip.Reason, StringComparison.Ordinal);
    }
}
