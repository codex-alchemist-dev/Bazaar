using Bazaar.Server.WorkLife;

namespace Bazaar.Server.Council.Eligibility;

/// <summary>
/// Vote deadline from open time (docs/design/10-council-and-voting.md, Eligibility, quorum and deadlines). Pure: no clock.
/// </summary>
/// <remarks>
/// Each voter's deadline is the end of the working day that closes their <see cref="VoteDeadlineSettings.WorkingDays"/>-th
/// working day after the open time, on their own schedule. A working day is a local date on which the voter's shift starts
/// (a curfew that splits a shift counts that date once). A shift already in progress at open does not count: "one working
/// day" means the next one.
/// Every per-voter deadline is capped at open plus <see cref="VoteDeadlineSettings.CapHours"/>.
/// The vote deadline is the EARLIEST per-voter deadline (the proposer-agreed rule).
/// The vote deadline is the cap when no working-day schedule resolves: when the voter has no schedule on file, when the
/// schedule has no shifts, or when the required working days do not finish before the cap. Each of these falls back to the cap.
/// </remarks>
public static class VoteDeadline
{
    /// <summary>The wall-clock cap for a vote opened at <paramref name="openedAt"/>.</summary>
    public static DateTimeOffset CapAt(DateTimeOffset openedAt, VoteDeadlineSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return openedAt + TimeSpan.FromHours(settings.CapHours);
    }

    /// <summary>
    /// The vote deadline: the earliest per-voter deadline among <paramref name="eligibleVoters"/>, or the cap when there are none.
    /// </summary>
    public static DateTimeOffset Compute(
        DateTimeOffset openedAt,
        IReadOnlySet<string> eligibleVoters,
        IVoterSchedules schedules,
        VoteDeadlineSettings settings)
    {
        ArgumentNullException.ThrowIfNull(eligibleVoters);
        ArgumentNullException.ThrowIfNull(schedules);
        ArgumentNullException.ThrowIfNull(settings);

        var earliest = CapAt(openedAt, settings);
        foreach (var voter in eligibleVoters)
        {
            var deadline = ForVoter(openedAt, voter, schedules, settings);
            if (deadline < earliest)
            {
                earliest = deadline;
            }
        }

        return earliest;
    }

    /// <summary>
    /// One voter's deadline on their own schedule, capped. Returns the cap when no working-day schedule resolves
    /// (no schedule on file, no shifts, or the working days do not finish before the cap).
    /// </summary>
    public static DateTimeOffset ForVoter(
        DateTimeOffset openedAt,
        string voter,
        IVoterSchedules schedules,
        VoteDeadlineSettings settings)
    {
        ArgumentNullException.ThrowIfNull(schedules);
        ArgumentNullException.ThrowIfNull(settings);

        var cap = CapAt(openedAt, settings);
        var inputs = schedules.GetInputs(voter);
        if (inputs is null || inputs.Schedule.Shifts.Count == 0)
        {
            // Fallback: no schedule resolves, so the cap applies.
            return cap;
        }

        var zone = inputs.Schedule.Zone;
        var countedDates = new HashSet<DateOnly>();
        var quotaReached = false;
        AvailabilityState? previous = null;
        var cursor = openedAt;

        // Walk the schedule boundary by boundary up to the cap. Each segment has one state; the next boundary ends it.
        while (cursor < cap)
        {
            var window = ScheduleEvaluator.Evaluate(inputs.Schedule, inputs.Events, cursor, inputs.Options);
            var onShift = window.State == AvailabilityState.OnShift;
            var wasOnShift = previous == AvailabilityState.OnShift;

            // A working day starts when a shift starts after open. The shift in progress at open is not counted.
            if (onShift && !wasOnShift && cursor > openedAt)
            {
                if (countedDates.Add(LocalDate(cursor, zone)) && countedDates.Count >= settings.WorkingDays)
                {
                    quotaReached = true;
                }
            }

            // The shift that completed the quota ends here: that instant is the voter's deadline.
            if (!onShift && wasOnShift && quotaReached)
            {
                return cursor;
            }

            previous = window.State;
            cursor = window.NextChangeAt is { } next && next < cap ? next : cap;
        }

        // Fallback: the working days do not finish before the cap.
        return cap;
    }

    private static DateOnly LocalDate(DateTimeOffset instant, TimeZoneInfo zone) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(instant, zone).DateTime);
}
