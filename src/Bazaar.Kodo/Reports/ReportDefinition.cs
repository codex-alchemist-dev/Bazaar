using Bazaar.Kodo.Export;

namespace Bazaar.Kodo.Reports;

/// <summary>A scheduled report (doc 46 section 16.7): an identifier and when it is due. What goes into it is fixed by the builder.</summary>
public sealed class ReportDefinition
{
    /// <summary>Creates a definition. The id is an identifier: lowercase letters, digits and underscores, starting with a letter, up to 64.</summary>
    public ReportDefinition(string id, ReportSchedule schedule)
    {
        ArgumentNullException.ThrowIfNull(schedule);
        if (string.IsNullOrEmpty(id) || !KodoExportPatterns.IdentifierName().IsMatch(id))
        {
            throw new ArgumentException("A report id must be an identifier.", nameof(id));
        }

        Id = id;
        Schedule = schedule;
    }

    /// <summary>The report id.</summary>
    public string Id { get; }

    /// <summary>When the report is due.</summary>
    public ReportSchedule Schedule { get; }
}

/// <summary>Limits on catching up reports missed while Kodo or the host was down, and on sinks. Safe defaults; the host may expose them as settings.</summary>
public sealed record ReportCatchUpOptions
{
    /// <summary>Most reports one run may build for missed slots. From 1 to 14.</summary>
    public int MaxCatchUpRuns { get; init; } = 3;

    /// <summary>How far back a missed slot is still worth building. From 1 to 92 days; older slots are skipped.</summary>
    public TimeSpan CatchUpWindow { get; init; } = TimeSpan.FromDays(31);

    /// <summary>The longest one sink may take before it counts as timed out.</summary>
    public TimeSpan SinkTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Throws <see cref="ArgumentOutOfRangeException"/> for a value outside its range.</summary>
    public void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxCatchUpRuns, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(MaxCatchUpRuns, 14);
        ArgumentOutOfRangeException.ThrowIfLessThan(CatchUpWindow, TimeSpan.FromDays(1));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(CatchUpWindow, TimeSpan.FromDays(92));
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(SinkTimeout, TimeSpan.Zero);
    }
}

/// <summary>
/// What is saved between runs for one report: the last due instant that was handled. The host keeps it and passes it back. Create the
/// first one with <see cref="Armed"/>.
/// </summary>
/// <param name="LastSlot">The last due instant that was handled. Slots after it and not after now are due.</param>
public sealed record ReportScheduleState(DateTimeOffset LastSlot)
{
    /// <summary>The state for a report that starts now: nothing before <paramref name="now"/> is due, so no old report is built.</summary>
    public static ReportScheduleState Armed(DateTimeOffset now) => new(now.ToUniversalTime());
}

/// <summary>The slots that are due now, after the catch-up bounds.</summary>
/// <param name="Due">The due instants to build, oldest first.</param>
/// <param name="Skipped">Missed slots left out because there were more than the cap.</param>
/// <param name="WindowTruncated">True when the saved state is older than the catch-up window, so older slots were not counted.</param>
/// <param name="NextDue">The next due instant after now.</param>
public sealed record ReportDueSet(IReadOnlyList<DateTimeOffset> Due, int Skipped, bool WindowTruncated, DateTimeOffset NextDue);

/// <summary>Works out which slots are due. Pure: time comes in as an argument.</summary>
public static class ReportScheduler
{
    /// <summary>Returns the slots after the saved state and not after now, newest <see cref="ReportCatchUpOptions.MaxCatchUpRuns"/> only, inside the catch-up window.</summary>
    public static ReportDueSet Evaluate(ReportSchedule schedule, ReportScheduleState state, DateTimeOffset now, ReportCatchUpOptions options)
    {
        ArgumentNullException.ThrowIfNull(schedule);
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        DateTimeOffset nowUtc = now.ToUniversalTime();
        DateTimeOffset floor = nowUtc - options.CatchUpWindow;
        bool truncated = state.LastSlot < floor;
        DateTimeOffset from = truncated ? floor : state.LastSlot;

        List<DateTimeOffset> slots = [];
        DateTimeOffset next = schedule.NextDue(from);
        while (next <= nowUtc)
        {
            slots.Add(next);
            next = schedule.NextDue(next);
        }

        int skipped = Math.Max(0, slots.Count - options.MaxCatchUpRuns);
        return new ReportDueSet(slots.GetRange(skipped, slots.Count - skipped), skipped, truncated, next);
    }
}
