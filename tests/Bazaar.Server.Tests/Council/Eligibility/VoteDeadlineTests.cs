using Bazaar.Server.Council.Eligibility;
using static Bazaar.Server.Tests.Council.Eligibility.EligibilityTestData;

namespace Bazaar.Server.Tests.Council.Eligibility;

public class VoteDeadlineTests
{
    // Monday 2026-11-02 10:00 UTC open. The default 72-hour cap is Thursday 2026-11-05 10:00 UTC.
    private static readonly DateTimeOffset _open = At("2026-11-02T10:00:00Z");
    private static readonly DateTimeOffset _cap = At("2026-11-05T10:00:00Z");

    private static readonly VoteDeadlineSettings _defaults = VoteDeadlineSettings.Defaults;

    [Fact]
    public void Default_settings_are_one_working_day_capped_at_72_hours()
    {
        Assert.Equal(1, _defaults.WorkingDays);
        Assert.Equal(72, _defaults.CapHours);
    }

    [Fact]
    public void Cap_is_open_plus_72_hours_for_the_default_settings()
    {
        Assert.Equal(_cap, VoteDeadline.CapAt(_open, _defaults));
    }

    [Fact]
    public void Deadline_is_capped_at_72_hours_when_the_working_day_falls_later()
    {
        // The voter only works on Sunday, which is beyond the cap.
        var schedules = new FakeVoterSchedules().Add("sunday", ShiftOn(DayOfWeek.Sunday));

        var deadline = VoteDeadline.Compute(_open, Set("sunday"), schedules, _defaults);

        Assert.Equal(_cap, deadline);
    }

    [Fact]
    public void Working_day_deadline_is_used_when_it_is_shorter_than_the_cap()
    {
        // Monday 10:00 is inside the Monday shift, so that shift does not count. Tuesday's shift ends 17:00.
        var schedules = new FakeVoterSchedules().Add("weekday", WeekdayShift());

        var deadline = VoteDeadline.Compute(_open, Set("weekday"), schedules, _defaults);

        Assert.Equal(At("2026-11-03T17:00:00Z"), deadline);
    }

    [Fact]
    public void Two_working_days_extend_the_deadline_to_the_second_shift_end()
    {
        var schedules = new FakeVoterSchedules().Add("weekday", WeekdayShift());

        var deadline = VoteDeadline.Compute(_open, Set("weekday"), schedules, new VoteDeadlineSettings(2, 72));

        Assert.Equal(At("2026-11-04T17:00:00Z"), deadline);
    }

    [Fact]
    public void Vacation_days_are_not_working_days()
    {
        // Tuesday is vacation, so the first working day after open is Wednesday.
        var schedules = new FakeVoterSchedules().Add(
            "holiday", WeekdayShift(), Vacation("2026-11-03T00:00:00Z", "2026-11-04T00:00:00Z"));

        var deadline = VoteDeadline.Compute(_open, Set("holiday"), schedules, _defaults);

        Assert.Equal(At("2026-11-04T17:00:00Z"), deadline);
    }

    [Fact]
    public void Earliest_per_voter_deadline_wins()
    {
        var schedules = new FakeVoterSchedules().Add("weekday", WeekdayShift());

        // The voter with no schedule falls back to the cap, which is later than Tuesday 17:00.
        var deadline = VoteDeadline.Compute(_open, Set("weekday", "unscheduled"), schedules, _defaults);

        Assert.Equal(At("2026-11-03T17:00:00Z"), deadline);
    }

    [Fact]
    public void No_schedule_on_file_falls_back_to_the_cap()
    {
        var deadline = VoteDeadline.Compute(_open, Set("unscheduled"), new FakeVoterSchedules(), _defaults);

        Assert.Equal(_cap, deadline);
    }

    [Fact]
    public void Schedule_with_no_shifts_falls_back_to_the_cap()
    {
        var schedules = new FakeVoterSchedules().Add("no-shifts", new("UTC", []));

        Assert.Equal(_cap, VoteDeadline.ForVoter(_open, "no-shifts", schedules, _defaults));
    }

    [Fact]
    public void Empty_electorate_gives_the_cap()
    {
        Assert.Equal(_cap, VoteDeadline.Compute(_open, Set(), new FakeVoterSchedules(), _defaults));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(73)]
    [InlineData(200)]
    public void Cap_outside_one_to_72_hours_is_rejected(int capHours) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new VoteDeadlineSettings(1, capHours));

    [Theory]
    [InlineData(1)]
    [InlineData(72)]
    public void Cap_of_one_and_72_hours_is_accepted(int capHours) =>
        Assert.Equal(capHours, new VoteDeadlineSettings(1, capHours).CapHours);

    [Theory]
    [InlineData(0)]
    [InlineData(-3)]
    public void Working_days_below_one_is_rejected(int workingDays) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new VoteDeadlineSettings(workingDays, 72));
}
