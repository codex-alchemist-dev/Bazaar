using Bazaar.Server.WorkLife;

namespace Bazaar.Server.Council.Eligibility;

/// <summary>
/// Quorum denominator at open, reduced for voters the work-life schedule shows absent (docs/design/10-council-and-voting.md,
/// Eligibility, quorum and deadlines; doc 09 for the schedule). Pure: the caller passes the window and the schedule inputs.
/// </summary>
/// <remarks>
/// A voter is excluded only when the schedule shows them not on shift (vacation, curfew or outside shift) at every instant
/// from open to deadline. Absence that is only self-declared never excludes anyone.
/// A voter with no schedule on file, or a schedule with no shifts, is never excluded: the schedule cannot show absence.
/// Exclusion applies to the quorum denominator only. Unanimity is measured over the set from <see cref="VoterEligibility"/>.
/// </remarks>
public static class AbsenceExclusion
{
    /// <summary>
    /// Returns the voters that stay in the quorum denominator.
    /// </summary>
    /// <param name="eligibleVoters">Eligible voters from <see cref="VoterEligibility"/>.</param>
    /// <param name="openedAt">When the vote opened. The window starts here.</param>
    /// <param name="deadlineAt">The vote deadline. The window is [openedAt, deadlineAt).</param>
    /// <param name="schedules">Schedule inputs for each voter.</param>
    /// <param name="selfDeclaredAbsent">
    /// Voters who declared themselves absent. Ignored by design: self-declared absence never reduces the denominator.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="deadlineAt"/> is not after <paramref name="openedAt"/>.</exception>
    public static IReadOnlySet<string> QuorumDenominator(
        IReadOnlySet<string> eligibleVoters,
        DateTimeOffset openedAt,
        DateTimeOffset deadlineAt,
        IVoterSchedules schedules,
        IReadOnlySet<string> selfDeclaredAbsent)
    {
        ArgumentNullException.ThrowIfNull(eligibleVoters);
        ArgumentNullException.ThrowIfNull(schedules);
        ArgumentNullException.ThrowIfNull(selfDeclaredAbsent);
        if (deadlineAt <= openedAt)
        {
            throw new ArgumentOutOfRangeException(nameof(deadlineAt), deadlineAt, "The deadline must be after the open time.");
        }

        var denominator = new HashSet<string>(StringComparer.Ordinal);
        foreach (var voter in eligibleVoters)
        {
            if (!IsAbsentForWholeWindow(voter, openedAt, deadlineAt, schedules))
            {
                denominator.Add(voter);
            }
        }

        return denominator;
    }

    /// <summary>
    /// Walks the schedule from open to deadline using the calculator's boundaries. Any instant on shift keeps the voter.
    /// </summary>
    private static bool IsAbsentForWholeWindow(
        string voter,
        DateTimeOffset openedAt,
        DateTimeOffset deadlineAt,
        IVoterSchedules schedules)
    {
        var inputs = schedules.GetInputs(voter);
        if (inputs is null || inputs.Schedule.Shifts.Count == 0)
        {
            return false;
        }

        var cursor = openedAt;
        while (cursor < deadlineAt)
        {
            var window = ScheduleEvaluator.Evaluate(inputs.Schedule, inputs.Events, cursor, inputs.Options);
            if (window.State == AvailabilityState.OnShift)
            {
                return false;
            }

            // The state holds until the next boundary; a null boundary means none within the calculator's horizon.
            cursor = window.NextChangeAt is { } next && next < deadlineAt ? next : deadlineAt;
        }

        return true;
    }
}
