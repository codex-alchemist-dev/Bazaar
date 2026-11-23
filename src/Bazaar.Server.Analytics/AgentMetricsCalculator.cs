namespace Bazaar.Server.Analytics;

/// <summary>
/// Pure metric maths. Same events in, same figures out, so a rollup can always be recomputed (doc 25, Behaviour). A rate over zero
/// samples is null: a missing source is a gap, never a zero.
/// </summary>
public static class AgentMetricsCalculator
{
    /// <summary>Revert rate above which the hint suggests earlier review help.</summary>
    internal const double RevertHintThreshold = 0.2;

    /// <summary>Review pass rate below which the hint suggests clearer briefs.</summary>
    internal const double ReviewHintThreshold = 0.5;

    private const int _minSamplesForHint = 3;

    public static AgentMetrics ForAgent(string agentId, IEnumerable<AgentWorkEvent> events, AnalyticsWindow window)
    {
        ArgumentNullException.ThrowIfNull(agentId);
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(window);

        var counts = Count(events.Where(e => e.AgentId == agentId && window.Contains(e.At)));
        return new AgentMetrics(
            agentId,
            counts.Tasks,
            PerWeek(counts.Tasks, window),
            counts.Passed,
            counts.Passed + counts.Changes,
            Rate(counts.Passed, counts.Passed + counts.Changes),
            counts.Merged,
            counts.Reverted,
            Rate(counts.Reverted, counts.Merged),
            Hints(counts));
    }

    public static CompanyMetrics ForCompany(IEnumerable<AgentWorkEvent> events, AnalyticsWindow window)
    {
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(window);

        var counts = Count(events.Where(e => window.Contains(e.At)));
        return new CompanyMetrics(
            counts.Tasks,
            PerWeek(counts.Tasks, window),
            Rate(counts.Passed, counts.Passed + counts.Changes),
            Rate(counts.Reverted, counts.Merged));
    }

    private static (int Tasks, int Passed, int Changes, int Merged, int Reverted) Count(IEnumerable<AgentWorkEvent> events)
    {
        int tasks = 0, passed = 0, changes = 0, merged = 0, reverted = 0;
        foreach (var e in events)
        {
            switch (e.Kind)
            {
                case AgentWorkEventKind.TaskCompleted: tasks++; break;
                case AgentWorkEventKind.ReviewPassed: passed++; break;
                case AgentWorkEventKind.ReviewChangesRequested: changes++; break;
                case AgentWorkEventKind.PrMerged: merged++; break;
                case AgentWorkEventKind.PrReverted: reverted++; break;
            }
        }

        return (tasks, passed, changes, merged, reverted);
    }

    private static double? Rate(int part, int whole) => whole == 0 ? null : (double)part / whole;

    private static double PerWeek(int count, AnalyticsWindow window) =>
        window.Length <= TimeSpan.Zero ? 0 : count / (window.Length.TotalDays / 7.0);

    /// <summary>Hints are phrased as what would help the company, never as a verdict on the agent.</summary>
    private static string[] Hints((int Tasks, int Passed, int Changes, int Merged, int Reverted) c)
    {
        var hints = new List<string>();
        if (c.Merged >= _minSamplesForHint && (double)c.Reverted / c.Merged > RevertHintThreshold)
        {
            hints.Add("Several merged changes were reverted. Earlier review, or a smaller first change, could help.");
        }

        var reviews = c.Passed + c.Changes;
        if (reviews >= _minSamplesForHint && (double)c.Passed / reviews < ReviewHintThreshold)
        {
            hints.Add("Reviews often ask for changes. A clearer brief or acceptance checklist could help.");
        }

        return [.. hints];
    }
}
