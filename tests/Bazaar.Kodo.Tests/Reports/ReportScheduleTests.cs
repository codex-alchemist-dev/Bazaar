using Bazaar.Kodo.Reports;

namespace Bazaar.Kodo.Tests.Reports;

public sealed class ReportScheduleTests
{
    private static DateTimeOffset Utc(int year, int month, int day, int hour = 0, int minute = 0) => new(year, month, day, hour, minute, 0, TimeSpan.Zero);

    // A US-like zone: standard time (-5) until the spring jump, daylight time (-4) until the autumn jump, then standard again.
    private static List<ReportOffsetRule> UsRules() =>
    [
        new(DateTimeOffset.UnixEpoch, TimeSpan.FromHours(-5)),
        new(Utc(2026, 3, 8, 7), TimeSpan.FromHours(-4)),
        new(Utc(2026, 11, 1, 6), TimeSpan.FromHours(-5)),
    ];

    [Fact]
    public void Daily_next_due_is_the_next_local_time_in_utc()
    {
        ReportSchedule schedule = ReportSchedule.Fixed(ReportCadence.Daily, TimeSpan.FromHours(8), TimeSpan.FromHours(1));

        Assert.Equal(Utc(2026, 11, 22, 7), schedule.NextDue(Utc(2026, 11, 22, 0)));
        Assert.Equal(Utc(2026, 11, 23, 7), schedule.NextDue(Utc(2026, 11, 22, 7)));
        Assert.Equal(Utc(2026, 11, 23, 7), schedule.NextDue(Utc(2026, 11, 22, 23)));
    }

    [Fact]
    public void Daily_with_a_negative_offset_can_land_on_the_next_utc_day()
    {
        ReportSchedule schedule = ReportSchedule.Fixed(ReportCadence.Daily, TimeSpan.FromHours(22), TimeSpan.FromHours(-5));

        Assert.Equal(Utc(2026, 11, 23, 3), schedule.NextDue(Utc(2026, 11, 22, 12)));
    }

    [Fact]
    public void Weekly_next_due_is_on_the_chosen_local_day()
    {
        ReportSchedule schedule = ReportSchedule.Fixed(ReportCadence.Weekly, TimeSpan.FromHours(9), TimeSpan.FromHours(-5), DayOfWeek.Monday);

        // 2026-11-22 is a Sunday. Monday 09:00 at -5 is 14:00 UTC.
        Assert.Equal(Utc(2026, 11, 23, 14), schedule.NextDue(Utc(2026, 11, 22, 12)));
        Assert.Equal(Utc(2026, 11, 30, 14), schedule.NextDue(Utc(2026, 11, 23, 14)));
    }

    [Fact]
    public void Previous_due_mirrors_next_due()
    {
        ReportSchedule schedule = ReportSchedule.Fixed(ReportCadence.Weekly, TimeSpan.FromHours(9), TimeSpan.FromHours(-5), DayOfWeek.Monday);
        DateTimeOffset due = Utc(2026, 11, 30, 14);

        Assert.Equal(Utc(2026, 11, 23, 14), schedule.PreviousDue(due));
        Assert.Equal(due, schedule.NextDue(schedule.PreviousDue(due)));
    }

    [Fact]
    public void Same_input_gives_the_same_answer()
    {
        ReportSchedule schedule = new(ReportCadence.Daily, TimeSpan.FromHours(2) + TimeSpan.FromMinutes(30), UsRules());

        Assert.Equal(schedule.NextDue(Utc(2026, 3, 1)), schedule.NextDue(Utc(2026, 3, 1)));
    }

    [Fact]
    public void Local_time_stays_the_same_across_the_spring_jump()
    {
        ReportSchedule schedule = new(ReportCadence.Daily, TimeSpan.FromHours(8), UsRules());

        Assert.Equal(Utc(2026, 3, 7, 13), schedule.NextDue(Utc(2026, 3, 7, 0)));
        Assert.Equal(Utc(2026, 3, 8, 12), schedule.NextDue(Utc(2026, 3, 7, 13)));
    }

    [Fact]
    public void A_local_time_skipped_by_the_spring_jump_is_due_when_the_clock_jumps()
    {
        ReportSchedule schedule = new(ReportCadence.Daily, TimeSpan.FromHours(2) + TimeSpan.FromMinutes(30), UsRules());

        Assert.Equal(Utc(2026, 3, 8, 7), schedule.NextDue(Utc(2026, 3, 8, 0)));
        Assert.Equal(Utc(2026, 3, 9, 6, 30), schedule.NextDue(Utc(2026, 3, 8, 7)));
    }

    [Fact]
    public void A_local_time_that_happens_twice_is_due_at_its_first_occurrence_only()
    {
        ReportSchedule schedule = new(ReportCadence.Daily, TimeSpan.FromHours(1) + TimeSpan.FromMinutes(30), UsRules());

        // 01:30 on 1 November 2026 is 05:30 UTC (daylight time) and again 06:30 UTC (standard time).
        DateTimeOffset first = schedule.NextDue(Utc(2026, 10, 31, 12));
        Assert.Equal(Utc(2026, 11, 1, 5, 30), first);
        Assert.Equal(Utc(2026, 11, 2, 6, 30), schedule.NextDue(first));
    }

    [Fact]
    public void Offset_at_follows_the_rules()
    {
        ReportSchedule schedule = new(ReportCadence.Daily, TimeSpan.Zero, UsRules());

        Assert.Equal(TimeSpan.FromHours(-5), schedule.OffsetAt(Utc(2026, 1, 1)));
        Assert.Equal(TimeSpan.FromHours(-4), schedule.OffsetAt(Utc(2026, 3, 8, 7)));
        Assert.Equal(TimeSpan.FromHours(-5), schedule.OffsetAt(Utc(2026, 11, 1, 6)));
        Assert.Equal(TimeSpan.FromHours(-5), schedule.OffsetAt(Utc(1999, 1, 1)));
    }

    [Fact]
    public void Invalid_schedules_are_refused()
    {
        ReportOffsetRule rule = new(DateTimeOffset.UnixEpoch, TimeSpan.Zero);

        Assert.Throws<ArgumentException>(() => new ReportSchedule(ReportCadence.Daily, TimeSpan.FromDays(1), [rule]));
        Assert.Throws<ArgumentException>(() => new ReportSchedule(ReportCadence.Daily, TimeSpan.FromSeconds(30), [rule]));
        Assert.Throws<ArgumentException>(() => new ReportSchedule(ReportCadence.Daily, TimeSpan.FromHours(1), []));
        Assert.Throws<ArgumentException>(() => new ReportSchedule(ReportCadence.Daily, TimeSpan.FromHours(1), [new ReportOffsetRule(DateTimeOffset.UnixEpoch, TimeSpan.FromHours(15))]));
        Assert.Throws<ArgumentException>(() => new ReportSchedule(ReportCadence.Daily, TimeSpan.FromHours(1), [new ReportOffsetRule(DateTimeOffset.UnixEpoch, TimeSpan.FromSeconds(10))]));
        Assert.Throws<ArgumentException>(() => new ReportSchedule(ReportCadence.Daily, TimeSpan.FromHours(1), [new ReportOffsetRule(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.FromHours(1)), TimeSpan.Zero)]));
        Assert.Throws<ArgumentException>(() => new ReportSchedule(
            ReportCadence.Daily,
            TimeSpan.FromHours(1),
            [new ReportOffsetRule(Utc(2026, 5, 1), TimeSpan.Zero), new ReportOffsetRule(Utc(2026, 4, 1), TimeSpan.Zero)]));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ReportSchedule(ReportCadence.Daily, TimeSpan.FromHours(1), [rule], (DayOfWeek)9));
    }

    [Fact]
    public void An_instant_out_of_range_is_refused()
    {
        ReportSchedule schedule = ReportSchedule.Fixed(ReportCadence.Daily, TimeSpan.FromHours(1), TimeSpan.Zero);

        Assert.Throws<ArgumentOutOfRangeException>(() => schedule.NextDue(Utc(1960, 1, 1)));
    }

    [Fact]
    public void Nothing_is_due_for_a_schedule_that_was_just_armed()
    {
        ReportSchedule schedule = ReportSchedule.Fixed(ReportCadence.Daily, TimeSpan.FromHours(6), TimeSpan.Zero);
        DateTimeOffset now = Utc(2026, 11, 22, 12);

        ReportDueSet due = ReportScheduler.Evaluate(schedule, ReportScheduleState.Armed(now), now, new ReportCatchUpOptions());

        Assert.Empty(due.Due);
        Assert.Equal(0, due.Skipped);
        Assert.Equal(Utc(2026, 11, 23, 6), due.NextDue);
    }

    [Fact]
    public void Missed_slots_are_caught_up_oldest_first_within_the_cap()
    {
        ReportSchedule schedule = ReportSchedule.Fixed(ReportCadence.Daily, TimeSpan.FromHours(6), TimeSpan.Zero);

        ReportDueSet due = ReportScheduler.Evaluate(schedule, new ReportScheduleState(Utc(2026, 11, 16, 6)), Utc(2026, 11, 22, 12), new ReportCatchUpOptions());

        Assert.Equal([Utc(2026, 11, 20, 6), Utc(2026, 11, 21, 6), Utc(2026, 11, 22, 6)], due.Due);
        Assert.Equal(3, due.Skipped);
        Assert.False(due.WindowTruncated);
    }

    [Fact]
    public void A_slot_exactly_at_now_is_due_and_one_a_moment_later_is_not()
    {
        ReportSchedule schedule = ReportSchedule.Fixed(ReportCadence.Daily, TimeSpan.FromHours(6), TimeSpan.Zero);
        ReportScheduleState state = new(Utc(2026, 11, 21, 6));

        Assert.Single(ReportScheduler.Evaluate(schedule, state, Utc(2026, 11, 22, 6), new ReportCatchUpOptions()).Due);
        Assert.Empty(ReportScheduler.Evaluate(schedule, state, Utc(2026, 11, 22, 6).AddTicks(-1), new ReportCatchUpOptions()).Due);
    }

    [Fact]
    public void A_state_older_than_the_window_is_flagged_and_never_floods()
    {
        ReportSchedule schedule = ReportSchedule.Fixed(ReportCadence.Daily, TimeSpan.FromHours(6), TimeSpan.Zero);

        ReportDueSet due = ReportScheduler.Evaluate(schedule, new ReportScheduleState(Utc(2020, 1, 1)), Utc(2026, 11, 22, 12), new ReportCatchUpOptions { MaxCatchUpRuns = 2 });

        Assert.True(due.WindowTruncated);
        Assert.Equal(2, due.Due.Count);
        Assert.Equal(Utc(2026, 11, 22, 6), due.Due[^1]);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(15)]
    public void Catch_up_options_out_of_range_are_refused(int runs)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ReportCatchUpOptions { MaxCatchUpRuns = runs }.Validate());
    }

    [Fact]
    public void Definition_ids_must_be_identifiers()
    {
        ReportSchedule schedule = ReportSchedule.Fixed(ReportCadence.Daily, TimeSpan.Zero, TimeSpan.Zero);

        Assert.Equal("daily_owner", new ReportDefinition("daily_owner", schedule).Id);
        Assert.Throws<ArgumentException>(() => new ReportDefinition("Daily Owner", schedule));
        Assert.Throws<ArgumentException>(() => new ReportDefinition(string.Empty, schedule));
    }
}
