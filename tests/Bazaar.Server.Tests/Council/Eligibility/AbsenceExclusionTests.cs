using Bazaar.Server.Council.Eligibility;
using Bazaar.Server.WorkLife;
using static Bazaar.Server.Tests.Council.Eligibility.EligibilityTestData;

namespace Bazaar.Server.Tests.Council.Eligibility;

public class AbsenceExclusionTests
{
    // Monday 2026-11-02 10:00 UTC open; Tuesday 2026-11-03 17:00 UTC deadline.
    private static readonly DateTimeOffset _open = At("2026-11-02T10:00:00Z");
    private static readonly DateTimeOffset _deadline = At("2026-11-03T17:00:00Z");

    [Fact]
    public void Voter_on_vacation_for_the_whole_window_is_excluded()
    {
        var schedules = new FakeVoterSchedules()
            .Add("away", WeekdayShift(), Vacation("2026-11-02T06:00:00Z", "2026-11-04T00:00:00Z"))
            .Add("here", WeekdayShift());

        var denominator = AbsenceExclusion.QuorumDenominator(
            Set("away", "here"), _open, _deadline, schedules, Set());

        Assert.Equal(["here"], Sorted(denominator));
    }

    [Fact]
    public void Voter_absent_for_part_of_the_window_is_not_excluded()
    {
        // Vacation covers only 10:00 to 12:00 on the open day; the shift then runs until 17:00.
        var schedules = new FakeVoterSchedules()
            .Add("partly", WeekdayShift(), Vacation("2026-11-02T10:00:00Z", "2026-11-02T12:00:00Z"));

        var denominator = AbsenceExclusion.QuorumDenominator(
            Set("partly"), _open, _deadline, schedules, Set());

        Assert.Equal(["partly"], Sorted(denominator));
    }

    [Fact]
    public void Voter_outside_shift_for_the_whole_window_is_excluded()
    {
        // A Saturday-only shift leaves the Monday to Tuesday window with no on-shift instant.
        var schedules = new FakeVoterSchedules().Add("weekend", ShiftOn(DayOfWeek.Saturday));

        var denominator = AbsenceExclusion.QuorumDenominator(
            Set("weekend"), _open, _deadline, schedules, Set());

        Assert.Empty(denominator);
    }

    [Fact]
    public void Self_declared_absence_is_ignored()
    {
        var schedules = new FakeVoterSchedules()
            .Add("claims-away", WeekdayShift())
            .Add("no-schedule", WeekdayShift());

        // Both voters are declared absent, but neither schedule shows absence for the window.
        var denominator = AbsenceExclusion.QuorumDenominator(
            Set("claims-away", "no-schedule"), _open, _deadline, schedules, Set("claims-away", "no-schedule"));

        Assert.Equal(["claims-away", "no-schedule"], Sorted(denominator));
    }

    [Fact]
    public void Self_declared_absence_with_no_schedule_on_file_keeps_the_voter()
    {
        var denominator = AbsenceExclusion.QuorumDenominator(
            Set("unscheduled"), _open, _deadline, new FakeVoterSchedules(), Set("unscheduled"));

        Assert.Equal(["unscheduled"], Sorted(denominator));
    }

    [Fact]
    public void Schedule_with_no_shifts_does_not_show_absence()
    {
        var schedules = new FakeVoterSchedules().Add("no-shifts", new AgentSchedule("UTC", []));

        var denominator = AbsenceExclusion.QuorumDenominator(
            Set("no-shifts"), _open, _deadline, schedules, Set());

        Assert.Equal(["no-shifts"], Sorted(denominator));
    }

    [Fact]
    public void Deadline_not_after_open_is_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            AbsenceExclusion.QuorumDenominator(Set("x"), _open, _open, new FakeVoterSchedules(), Set()));
    }
}
