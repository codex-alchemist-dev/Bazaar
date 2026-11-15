using Bazaar.RyuseiFuda.Orrery;

namespace Bazaar.Server.Tests.Kanban.Ryusei;

/// <summary>Next-fire arithmetic for timers and intervals (doc 38, Trigger types and Missed fires). Fixed times, no clock.</summary>
public sealed class RyuseiScheduleTests
{
    private static readonly TimeZoneInfo _berlin = TimeZoneInfo.FindSystemTimeZoneById("Europe/Berlin");
    private static readonly TimeSpan _floor = TimeSpan.FromMinutes(5);

    private static DateTimeOffset Utc(int year, int month, int day, int hour, int minute) =>
        new(year, month, day, hour, minute, 0, TimeSpan.Zero);

    [Fact]
    public void Timer_in_the_past_fires_once_with_no_missed_count()
    {
        var fireAt = Utc(2026, 11, 1, 9, 0);

        var result = RyuseiSchedule.NextFire(new RyuseiTimerTriggerSpec(fireAt), Utc(2026, 11, 1, 12, 0));

        Assert.True(result.IsDue);
        Assert.Equal(fireAt, result.ScheduledFor);
        Assert.Equal(0, result.MissedCount);
    }

    [Fact]
    public void Timer_in_the_future_is_not_due()
    {
        var fireAt = Utc(2026, 11, 1, 9, 0);

        var result = RyuseiSchedule.NextFire(new RyuseiTimerTriggerSpec(fireAt), Utc(2026, 11, 1, 8, 59));

        Assert.False(result.IsDue);
        Assert.Null(result.ScheduledFor);
        Assert.Equal(fireAt, result.NextRunAt);
    }

    [Fact]
    public void Timer_at_exactly_now_is_due()
    {
        var fireAt = Utc(2026, 11, 1, 9, 0);

        var result = RyuseiSchedule.NextFire(new RyuseiTimerTriggerSpec(fireAt), fireAt);

        Assert.True(result.IsDue);
    }

    public static TheoryData<string, DateTimeOffset, DateTimeOffset?, bool, DateTimeOffset?, long, DateTimeOffset> IntervalCases => new()
    {
        // name, now, lastScheduledFor, IsDue, ScheduledFor, MissedCount, NextRunAt
        { "before anchor is not due", Utc(2026, 11, 1, 9, 50), null, false, null, 0, Utc(2026, 11, 1, 10, 0) },
        { "first occurrence at anchor is due", Utc(2026, 11, 1, 10, 0), null, true, Utc(2026, 11, 1, 10, 0), 0, Utc(2026, 11, 1, 10, 30) },
        { "between occurrences after a fire is not due", Utc(2026, 11, 1, 10, 20), Utc(2026, 11, 1, 10, 0), false, null, 0, Utc(2026, 11, 1, 10, 30) },
        { "exactly on an occurrence after a fire is due once", Utc(2026, 11, 1, 10, 30), Utc(2026, 11, 1, 10, 0), true, Utc(2026, 11, 1, 10, 30), 0, Utc(2026, 11, 1, 11, 0) },
        // Downtime: the server was away from 10:00 to 12:10. Four runs were missed and coalesce into one fire at 12:00.
        { "downtime coalesces into one fire", Utc(2026, 11, 1, 12, 10), Utc(2026, 11, 1, 10, 0), true, Utc(2026, 11, 1, 12, 0), 3, Utc(2026, 11, 1, 12, 30) },
        // Never fired: occurrences 10:00 to 12:00 are all due, so 4 are missed.
        { "never fired, anchor in the past, coalesces", Utc(2026, 11, 1, 12, 10), null, true, Utc(2026, 11, 1, 12, 0), 4, Utc(2026, 11, 1, 12, 30) },
    };

    [Theory]
    [MemberData(nameof(IntervalCases))]
    public void Interval_next_fire_coalesces_missed_runs(
        string name,
        DateTimeOffset now,
        DateTimeOffset? lastScheduledFor,
        bool isDue,
        DateTimeOffset? scheduledFor,
        long missedCount,
        DateTimeOffset nextRunAt)
    {
        var spec = new RyuseiIntervalTriggerSpec(TimeSpan.FromMinutes(30), Utc(2026, 11, 1, 10, 0));

        var result = RyuseiSchedule.NextFire(spec, now, _berlin, _floor, lastScheduledFor);

        Assert.True(result.IsDue == isDue, name);
        Assert.Equal(scheduledFor, result.ScheduledFor);
        Assert.Equal(missedCount, result.MissedCount);
        Assert.Equal(nextRunAt, result.NextRunAt);
    }

    [Fact]
    public void Daily_interval_keeps_wall_clock_time_across_spring_forward()
    {
        // Anchor: 09:00 _berlin on 2026-03-27 (CET, +01:00). Daily at 09:00 local.
        // Spring forward is 2026-03-29 02:00 CET to 03:00 CEST, so 09:00 local is 07:00Z from then on.
        var spec = new RyuseiIntervalTriggerSpec(TimeSpan.FromDays(1), Utc(2026, 3, 27, 8, 0));

        var beforeSwitch = RyuseiSchedule.NextFire(spec, Utc(2026, 3, 28, 12, 0), _berlin, _floor, Utc(2026, 3, 27, 8, 0));
        Assert.Equal(Utc(2026, 3, 29, 7, 0), beforeSwitch.NextRunAt);
        Assert.True(beforeSwitch.IsDue);
        Assert.Equal(Utc(2026, 3, 28, 8, 0), beforeSwitch.ScheduledFor);

        var afterSwitch = RyuseiSchedule.NextFire(spec, Utc(2026, 3, 30, 6, 30), _berlin, _floor, lastScheduledFor: null);
        Assert.True(afterSwitch.IsDue);
        Assert.Equal(Utc(2026, 3, 29, 7, 0), afterSwitch.ScheduledFor);
        Assert.Equal(2, afterSwitch.MissedCount);
        Assert.Equal(Utc(2026, 3, 30, 7, 0), afterSwitch.NextRunAt);
    }

    [Fact]
    public void Daily_interval_keeps_wall_clock_time_across_fall_back()
    {
        // Fall back is 2026-10-25 03:00 CEST to 02:00 CET. Anchor 09:00 _berlin on 2026-10-23 is 07:00Z (CEST).
        var spec = new RyuseiIntervalTriggerSpec(TimeSpan.FromDays(1), Utc(2026, 10, 23, 7, 0));

        var result = RyuseiSchedule.NextFire(spec, Utc(2026, 10, 24, 7, 30), _berlin, _floor, lastScheduledFor: Utc(2026, 10, 24, 7, 0));

        Assert.False(result.IsDue);
        Assert.Equal(Utc(2026, 10, 25, 8, 0), result.NextRunAt);
    }

    [Fact]
    public void Daily_interval_in_a_spring_forward_gap_moves_forward_by_the_gap()
    {
        // 02:30 local does not exist on 2026-03-29 in _berlin. The run moves to 03:30 CEST, which is 01:30Z.
        var spec = new RyuseiIntervalTriggerSpec(TimeSpan.FromDays(1), Utc(2026, 3, 28, 1, 30));

        var result = RyuseiSchedule.NextFire(spec, Utc(2026, 3, 28, 12, 0), _berlin, _floor, lastScheduledFor: Utc(2026, 3, 28, 1, 30));

        Assert.Equal(Utc(2026, 3, 29, 1, 30), result.NextRunAt);
    }

    [Theory]
    [InlineData(59, 5)]
    [InlineData(4, 5)]
    public void Interval_below_the_configured_minimum_is_refused(int everySeconds, int minMinutes)
    {
        var decision = RyuseiSchedule.CheckIntervalFloor(TimeSpan.FromSeconds(everySeconds), TimeSpan.FromMinutes(minMinutes));

        Assert.False(decision.IsAllowed);
        Assert.Equal(RyuseiRefusal.IntervalBelowFloor, decision.Refusal);
    }

    [Theory]
    [InlineData(300, 5)]
    [InlineData(301, 5)]
    [InlineData(60, 1)]
    public void Interval_at_or_above_the_configured_minimum_is_allowed(int everySeconds, int minMinutes)
    {
        Assert.True(RyuseiSchedule.CheckIntervalFloor(TimeSpan.FromSeconds(everySeconds), TimeSpan.FromMinutes(minMinutes)).IsAllowed);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(10)]
    [InlineData(60)]
    public void Configured_minimum_below_the_hard_floor_is_raised_to_one_minute(int minSeconds)
    {
        Assert.Equal(TimeSpan.FromMinutes(1), RyuseiSchedule.EffectiveIntervalFloor(TimeSpan.FromSeconds(minSeconds)));
        Assert.False(RyuseiSchedule.CheckIntervalFloor(TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(minSeconds)).IsAllowed);
    }

    [Fact]
    public void Next_fire_throws_for_an_interval_below_the_floor()
    {
        var spec = new RyuseiIntervalTriggerSpec(TimeSpan.FromMinutes(4), Utc(2026, 11, 1, 10, 0));

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            RyuseiSchedule.NextFire(spec, Utc(2026, 11, 1, 11, 0), _berlin, _floor));
    }
}
