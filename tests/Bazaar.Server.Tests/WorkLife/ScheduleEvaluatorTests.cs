using System.Globalization;
using Bazaar.Server.WorkLife;

namespace Bazaar.Server.Tests.WorkLife;

public class ScheduleEvaluatorTests
{
    private static readonly Guid _holidayId = Guid.Parse("0196a6f0-0000-7000-8000-000000000201");
    private static readonly Guid _meetingId = Guid.Parse("0196a6f0-0000-7000-8000-000000000202");

    private static DateTimeOffset At(string iso) =>
        DateTimeOffset.Parse(iso, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);

    private static ShiftWindow Shift(DayOfWeek[] days, int startHour, int startMinute, int endHour, int endMinute) =>
        new(new HashSet<DayOfWeek>(days), new TimeOnly(startHour, startMinute), new TimeOnly(endHour, endMinute));

    private static CurfewWindow Curfew(int startHour, int endHour) =>
        new(new TimeOnly(startHour, 0), new TimeOnly(endHour, 0));

    private static CalendarEvent Event(Guid id, string kind, string start, string end, bool affects) =>
        new(id, kind, At(start), At(end), affects);

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

    private static readonly DayOfWeek[] _weekdays =
        [DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday];

    [Theory]
    [InlineData("Not/AZone")]
    [InlineData("")]
    [InlineData(" ")]
    public void Invalid_zone_is_rejected(string tz) =>
        Assert.ThrowsAny<ArgumentException>(() => new AgentSchedule(tz, []));

    [Fact]
    public void Null_zone_is_rejected() =>
        Assert.ThrowsAny<ArgumentException>(() => new AgentSchedule(null!, []));

    [Fact]
    public void Shift_with_equal_start_and_end_is_rejected() =>
        Assert.Throws<ArgumentException>(() => Shift(_weekdays, 9, 0, 9, 0));

    [Fact]
    public void Event_that_does_not_end_after_it_starts_is_rejected() =>
        Assert.Throws<ArgumentException>(() =>
            Event(_holidayId, "holiday", "2026-06-15T10:00:00Z", "2026-06-15T10:00:00Z", true));

    [Fact]
    public void Inside_a_shift_is_on_shift_with_the_shift_end_as_next_change()
    {
        var schedule = new AgentSchedule("Europe/London", [Shift(_weekdays, 9, 0, 17, 0)]);

        // Monday 2026-06-15 10:00 BST.
        var window = ScheduleEvaluator.Evaluate(schedule, [], At("2026-06-15T09:00:00Z"));

        Assert.Equal(new AvailabilityWindow(AvailabilityState.OnShift, "shift", At("2026-06-15T16:00:00Z")), window);
    }

    [Fact]
    public void Shift_start_is_inclusive_and_end_is_exclusive()
    {
        var schedule = new AgentSchedule("Europe/London", [Shift(_weekdays, 9, 0, 17, 0)]);

        Assert.Equal(AvailabilityState.Off, ScheduleEvaluator.Evaluate(schedule, [], At("2026-06-15T07:59:59Z")).State);
        Assert.Equal(AvailabilityState.OnShift, ScheduleEvaluator.Evaluate(schedule, [], At("2026-06-15T08:00:00Z")).State);
        Assert.Equal(AvailabilityState.OnShift, ScheduleEvaluator.Evaluate(schedule, [], At("2026-06-15T15:59:59Z")).State);
        Assert.Equal(AvailabilityState.Off, ScheduleEvaluator.Evaluate(schedule, [], At("2026-06-15T16:00:00Z")).State);
    }

    [Fact]
    public void Outside_a_shift_is_off_with_the_next_shift_start_as_next_change()
    {
        var schedule = new AgentSchedule("Europe/London", [Shift(_weekdays, 9, 0, 17, 0)]);

        // Monday 2026-06-15 06:00 BST, before the day's shift.
        var window = ScheduleEvaluator.Evaluate(schedule, [], At("2026-06-15T05:00:00Z"));

        Assert.Equal(new AvailabilityWindow(AvailabilityState.Off, "outside-shift", At("2026-06-15T08:00:00Z")), window);
    }

    [Fact]
    public void Shift_on_an_unlisted_day_does_not_apply()
    {
        var schedule = new AgentSchedule("Europe/London", [Shift(_weekdays, 9, 0, 17, 0)]);

        // Saturday 2026-06-20 10:00 BST.
        var window = ScheduleEvaluator.Evaluate(schedule, [], At("2026-06-20T09:00:00Z"));

        Assert.Equal(AvailabilityState.Off, window.State);
        Assert.Equal("outside-shift", window.Reason);
    }

    [Fact]
    public void Shift_crossing_midnight_is_anchored_on_its_start_day()
    {
        // Monday 22:00 to 02:00, in Europe/London (BST, UTC+1).
        var schedule = new AgentSchedule("Europe/London", [Shift([DayOfWeek.Monday], 22, 0, 2, 0)]);

        // Tuesday 01:00 BST is inside Monday's shift.
        Assert.Equal(AvailabilityState.OnShift, ScheduleEvaluator.Evaluate(schedule, [], At("2026-06-16T00:00:00Z")).State);

        // Tuesday 02:00 BST is the exclusive end.
        var atEnd = ScheduleEvaluator.Evaluate(schedule, [], At("2026-06-16T01:00:00Z"));
        Assert.Equal(AvailabilityState.Off, atEnd.State);
        Assert.Equal(At("2026-06-22T21:00:00Z"), atEnd.NextChangeAt);

        // Tuesday 23:00 BST does not start a shift, because Tuesday is not a shift day.
        Assert.Equal(AvailabilityState.Off, ScheduleEvaluator.Evaluate(schedule, [], At("2026-06-16T22:00:00Z")).State);
    }

    [Fact]
    public void Curfew_crossing_midnight_is_off_on_both_sides_of_midnight()
    {
        var schedule = new AgentSchedule("Europe/London", [Shift(_weekdays, 8, 0, 20, 0)], Curfew(23, 7));

        // 23:30 BST on Monday: the shift ended at 20:00, and the curfew (23:00 to 07:00) has started.
        var lateEvening = ScheduleEvaluator.Evaluate(schedule, [], At("2026-06-15T22:30:00Z"));
        Assert.Equal(new AvailabilityWindow(AvailabilityState.Off, "curfew", At("2026-06-16T06:00:00Z")), lateEvening);

        // 06:30 BST the next day.
        var earlyMorning = ScheduleEvaluator.Evaluate(schedule, [], At("2026-06-16T05:30:00Z"));
        Assert.Equal("curfew", earlyMorning.Reason);

        // 07:00 BST ends the curfew; the shift has not started yet.
        Assert.Equal("outside-shift", ScheduleEvaluator.Evaluate(schedule, [], At("2026-06-16T06:00:00Z")).Reason);
    }

    [Fact]
    public void Curfew_beats_shift()
    {
        var schedule = new AgentSchedule("Europe/London", [Shift(_weekdays, 8, 0, 20, 0)], Curfew(18, 22));

        var window = ScheduleEvaluator.Evaluate(schedule, [], At("2026-06-15T18:00:00Z"));

        Assert.Equal(AvailabilityState.Off, window.State);
        Assert.Equal("curfew", window.Reason);
    }

    [Fact]
    public void Vacation_beats_curfew_and_shift_and_names_the_event()
    {
        var schedule = new AgentSchedule("Europe/London", [Shift(_weekdays, 8, 0, 20, 0)], Curfew(18, 22));
        var events = new[]
        {
            Event(_holidayId, "holiday", "2026-06-15T00:00:00Z", "2026-06-17T00:00:00Z", true),
        };

        var window = ScheduleEvaluator.Evaluate(schedule, events, At("2026-06-15T18:30:00Z"));

        Assert.Equal(AvailabilityState.Vacation, window.State);
        Assert.Equal($"vacation:{_holidayId}", window.Reason);
    }

    [Fact]
    public void Vacation_start_is_inclusive_and_end_is_exclusive()
    {
        var schedule = new AgentSchedule("Europe/London", [Shift(_weekdays, 8, 0, 20, 0)]);
        var events = new[]
        {
            Event(_holidayId, "holiday", "2026-06-15T10:00:00Z", "2026-06-15T12:00:00Z", true),
        };

        Assert.Equal(AvailabilityState.OnShift, ScheduleEvaluator.Evaluate(schedule, events, At("2026-06-15T09:59:59Z")).State);
        Assert.Equal(AvailabilityState.Vacation, ScheduleEvaluator.Evaluate(schedule, events, At("2026-06-15T10:00:00Z")).State);
        Assert.Equal(AvailabilityState.Vacation, ScheduleEvaluator.Evaluate(schedule, events, At("2026-06-15T11:59:59Z")).State);
        Assert.Equal(AvailabilityState.OnShift, ScheduleEvaluator.Evaluate(schedule, events, At("2026-06-15T12:00:00Z")).State);
    }

    [Fact]
    public void Event_that_does_not_affect_availability_is_ignored()
    {
        var schedule = new AgentSchedule("Europe/London", [Shift(_weekdays, 8, 0, 20, 0)]);
        var events = new[]
        {
            Event(_meetingId, "meeting", "2026-06-15T09:00:00Z", "2026-06-15T10:00:00Z", false),
        };

        var window = ScheduleEvaluator.Evaluate(schedule, events, At("2026-06-15T09:30:00Z"));

        Assert.Equal(new AvailabilityWindow(AvailabilityState.OnShift, "shift", At("2026-06-15T19:00:00Z")), window);
    }

    [Fact]
    public void Extra_availability_kind_from_options_makes_a_vacation()
    {
        var schedule = new AgentSchedule("Europe/London", [Shift(_weekdays, 8, 0, 20, 0)]);
        var events = new[]
        {
            Event(_holidayId, "holiday", "2026-06-15T09:00:00Z", "2026-06-15T10:00:00Z", false),
        };
        var options = new EvaluationOptions { ExtraAvailabilityKinds = new HashSet<string> { "holiday" } };

        var window = ScheduleEvaluator.Evaluate(schedule, events, At("2026-06-15T09:30:00Z"), options);

        Assert.Equal(AvailabilityState.Vacation, window.State);
        Assert.Equal($"vacation:{_holidayId}", window.Reason);
        Assert.Equal(AvailabilityState.OnShift, ScheduleEvaluator.Evaluate(schedule, events, At("2026-06-15T09:30:00Z")).State);
    }

    [Fact]
    public void Earliest_covering_vacation_gives_the_reason()
    {
        var schedule = new AgentSchedule("Europe/London", [Shift(_weekdays, 8, 0, 20, 0)]);
        var later = Event(_meetingId, "holiday", "2026-06-15T09:00:00Z", "2026-06-15T12:00:00Z", true);
        var earlier = Event(_holidayId, "holiday", "2026-06-15T08:00:00Z", "2026-06-15T12:00:00Z", true);

        var window = ScheduleEvaluator.Evaluate(schedule, [later, earlier], At("2026-06-15T09:30:00Z"));

        Assert.Equal($"vacation:{_holidayId}", window.Reason);
    }

    [Fact]
    public void Next_change_is_null_when_no_boundary_is_within_the_horizon()
    {
        var schedule = new AgentSchedule("Europe/London", []);

        var window = ScheduleEvaluator.Evaluate(schedule, [], At("2026-06-15T09:00:00Z"));

        Assert.Equal(new AvailabilityWindow(AvailabilityState.Off, "outside-shift", null), window);
    }

    [Fact]
    public void Next_change_ignores_a_vacation_starting_after_eight_days()
    {
        var schedule = new AgentSchedule("Europe/London", []);
        var events = new[]
        {
            Event(_holidayId, "holiday", "2026-06-24T10:00:00Z", "2026-06-25T10:00:00Z", true),
        };

        var window = ScheduleEvaluator.Evaluate(schedule, events, At("2026-06-15T09:00:00Z"));

        Assert.Null(window.NextChangeAt);
    }

    [Fact]
    public void Next_change_includes_a_vacation_starting_within_eight_days()
    {
        var schedule = new AgentSchedule("Europe/London", []);
        var events = new[]
        {
            Event(_holidayId, "holiday", "2026-06-22T10:00:00Z", "2026-06-23T10:00:00Z", true),
        };

        var window = ScheduleEvaluator.Evaluate(schedule, events, At("2026-06-15T09:00:00Z"));

        Assert.Equal(At("2026-06-22T10:00:00Z"), window.NextChangeAt);
    }

    [Fact]
    public void Next_change_is_strictly_after_now()
    {
        var schedule = new AgentSchedule("Europe/London", [Shift(_weekdays, 9, 0, 17, 0)]);

        // Exactly at the shift start: the current state begins here, so the next change is the shift end.
        var window = ScheduleEvaluator.Evaluate(schedule, [], At("2026-06-15T08:00:00Z"));

        Assert.Equal(AvailabilityState.OnShift, window.State);
        Assert.Equal(At("2026-06-15T16:00:00Z"), window.NextChangeAt);
    }

    [Fact]
    public void Next_change_is_the_earliest_of_shift_curfew_and_vacation_boundaries()
    {
        var schedule = new AgentSchedule("Europe/London", [Shift(_weekdays, 9, 0, 17, 0)], Curfew(23, 7));
        var events = new[]
        {
            Event(_holidayId, "holiday", "2026-06-15T13:00:00Z", "2026-06-15T14:00:00Z", true),
        };

        // Monday 10:00 BST: the vacation at 13:00Z comes before the shift end at 16:00Z.
        var window = ScheduleEvaluator.Evaluate(schedule, events, At("2026-06-15T09:00:00Z"));

        Assert.Equal(At("2026-06-15T13:00:00Z"), window.NextChangeAt);
    }

    [Fact]
    public void Curfew_across_midnight_gives_the_correct_next_change_from_the_evening()
    {
        var schedule = new AgentSchedule("Europe/London", [], Curfew(23, 7));

        // 21:00 BST, before the curfew starts at 23:00.
        var window = ScheduleEvaluator.Evaluate(schedule, [], At("2026-06-15T20:00:00Z"));

        Assert.Equal(AvailabilityState.Off, window.State);
        Assert.Equal("outside-shift", window.Reason);
        Assert.Equal(At("2026-06-15T22:00:00Z"), window.NextChangeAt);
    }

    [Fact]
    public void New_York_spring_gap_moves_a_shift_start_forward_by_the_gap()
    {
        RequireZone("America/New_York");

        // 2026-03-08 (Sunday): 02:00 EST jumps to 03:00 EDT. A shift nominally from 02:30 to 04:00 local.
        var schedule = new AgentSchedule("America/New_York", [Shift([DayOfWeek.Sunday], 2, 30, 4, 0)]);

        // 02:30 does not exist. It becomes the instant 02:30 EST (07:30Z), which reads as 03:30 EDT.
        Assert.Equal(AvailabilityState.Off, ScheduleEvaluator.Evaluate(schedule, [], At("2026-03-08T06:59:00Z")).State);
        var before = ScheduleEvaluator.Evaluate(schedule, [], At("2026-03-08T07:29:00Z"));
        Assert.Equal(AvailabilityState.Off, before.State);
        Assert.Equal(At("2026-03-08T07:30:00Z"), before.NextChangeAt);

        var start = ScheduleEvaluator.Evaluate(schedule, [], At("2026-03-08T07:30:00Z"));
        Assert.Equal(AvailabilityState.OnShift, start.State);
        Assert.Equal(At("2026-03-08T08:00:00Z"), start.NextChangeAt);
        Assert.Equal(AvailabilityState.Off, ScheduleEvaluator.Evaluate(schedule, [], At("2026-03-08T08:00:00Z")).State);
    }

    [Fact]
    public void New_York_fall_overlap_uses_the_first_occurrence()
    {
        RequireZone("America/New_York");

        // 2026-11-01 (Sunday): 02:00 EDT falls back to 01:00 EST, so 01:30 happens twice.
        var schedule = new AgentSchedule("America/New_York", [Shift([DayOfWeek.Sunday], 1, 30, 2, 30)]);

        // First 01:30 is 01:30 EDT (05:30Z). The shift starts there, not at the second 01:30 (06:30Z).
        var before = ScheduleEvaluator.Evaluate(schedule, [], At("2026-11-01T05:29:00Z"));
        Assert.Equal(AvailabilityState.Off, before.State);
        Assert.Equal(At("2026-11-01T05:30:00Z"), before.NextChangeAt);

        Assert.Equal(AvailabilityState.OnShift, ScheduleEvaluator.Evaluate(schedule, [], At("2026-11-01T05:30:00Z")).State);

        // 06:45Z is 01:45 EST, the second pass through the window, and is still in the shift.
        Assert.Equal(AvailabilityState.OnShift, ScheduleEvaluator.Evaluate(schedule, [], At("2026-11-01T06:45:00Z")).State);

        // 02:30 EST is 07:30Z, the exclusive end.
        Assert.Equal(AvailabilityState.Off, ScheduleEvaluator.Evaluate(schedule, [], At("2026-11-01T07:30:00Z")).State);
    }

    [Fact]
    public void London_spring_gap_moves_a_shift_start_forward_by_the_gap()
    {
        RequireZone("Europe/London");

        // 2026-03-29 (Sunday): 01:00 GMT jumps to 02:00 BST. A shift nominally from 01:30 to 03:00 local.
        var schedule = new AgentSchedule("Europe/London", [Shift([DayOfWeek.Sunday], 1, 30, 3, 0)]);

        // 01:30 does not exist; it becomes 01:30Z, which reads as 02:30 BST.
        Assert.Equal(AvailabilityState.Off, ScheduleEvaluator.Evaluate(schedule, [], At("2026-03-29T01:29:00Z")).State);
        Assert.Equal(AvailabilityState.OnShift, ScheduleEvaluator.Evaluate(schedule, [], At("2026-03-29T01:30:00Z")).State);

        // 03:00 BST is 02:00Z, the exclusive end.
        Assert.Equal(AvailabilityState.Off, ScheduleEvaluator.Evaluate(schedule, [], At("2026-03-29T02:00:00Z")).State);
    }

    [Fact]
    public void London_fall_overlap_uses_the_first_occurrence()
    {
        RequireZone("Europe/London");

        // 2026-10-25 (Sunday): 02:00 BST falls back to 01:00 GMT, so 01:30 happens twice.
        var schedule = new AgentSchedule("Europe/London", [Shift([DayOfWeek.Sunday], 1, 30, 2, 30)]);

        // First 01:30 is BST (00:30Z).
        var before = ScheduleEvaluator.Evaluate(schedule, [], At("2026-10-25T00:29:00Z"));
        Assert.Equal(AvailabilityState.Off, before.State);
        Assert.Equal(At("2026-10-25T00:30:00Z"), before.NextChangeAt);
        Assert.Equal(AvailabilityState.OnShift, ScheduleEvaluator.Evaluate(schedule, [], At("2026-10-25T00:30:00Z")).State);

        // 02:30 GMT is 02:30Z, the exclusive end.
        Assert.Equal(AvailabilityState.OnShift, ScheduleEvaluator.Evaluate(schedule, [], At("2026-10-25T02:29:00Z")).State);
        Assert.Equal(AvailabilityState.Off, ScheduleEvaluator.Evaluate(schedule, [], At("2026-10-25T02:30:00Z")).State);
    }

    [Fact]
    public void Lord_Howe_half_hour_spring_gap_moves_by_thirty_minutes()
    {
        RequireZone("Australia/Lord_Howe");

        // 2026-10-04 (Sunday): 02:00 at +10:30 becomes 02:30 at +11:00. A shift nominally from 02:15 to 03:00.
        var schedule = new AgentSchedule("Australia/Lord_Howe", [Shift([DayOfWeek.Sunday], 2, 15, 3, 0)]);

        // 02:15 does not exist; it becomes 02:15 at +10:30 (15:45Z the day before), which reads as 02:45 at +11:00.
        Assert.Equal(AvailabilityState.Off, ScheduleEvaluator.Evaluate(schedule, [], At("2026-10-03T15:44:00Z")).State);
        Assert.Equal(AvailabilityState.OnShift, ScheduleEvaluator.Evaluate(schedule, [], At("2026-10-03T15:45:00Z")).State);

        // 03:00 at +11:00 is 16:00Z, the exclusive end.
        Assert.Equal(AvailabilityState.OnShift, ScheduleEvaluator.Evaluate(schedule, [], At("2026-10-03T15:59:00Z")).State);
        Assert.Equal(AvailabilityState.Off, ScheduleEvaluator.Evaluate(schedule, [], At("2026-10-03T16:00:00Z")).State);
    }

    [Fact]
    public void Lord_Howe_half_hour_fall_overlap_uses_the_first_occurrence()
    {
        RequireZone("Australia/Lord_Howe");

        // 2026-04-05 (Sunday): 02:00 at +11:00 falls back to 01:30 at +10:30, so 01:30 to 02:00 happens twice.
        var schedule = new AgentSchedule("Australia/Lord_Howe", [Shift([DayOfWeek.Sunday], 1, 45, 2, 15)]);

        // First 01:45 is at +11:00 (14:45Z the day before).
        Assert.Equal(AvailabilityState.Off, ScheduleEvaluator.Evaluate(schedule, [], At("2026-04-04T14:44:00Z")).State);
        Assert.Equal(AvailabilityState.OnShift, ScheduleEvaluator.Evaluate(schedule, [], At("2026-04-04T14:45:00Z")).State);

        // 02:15 at +10:30 is 15:45Z, the exclusive end.
        Assert.Equal(AvailabilityState.OnShift, ScheduleEvaluator.Evaluate(schedule, [], At("2026-04-04T15:44:00Z")).State);
        Assert.Equal(AvailabilityState.Off, ScheduleEvaluator.Evaluate(schedule, [], At("2026-04-04T15:45:00Z")).State);
    }
}
