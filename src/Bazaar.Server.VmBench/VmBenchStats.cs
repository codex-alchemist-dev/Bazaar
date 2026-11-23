using Bazaar.Server.VmBench.Abstractions;

namespace Bazaar.Server.VmBench;

/// <summary>
/// Summary statistics per (scenario, guest OS, unit). Only successful samples feed the numbers; failed samples are counted
/// separately so a run that failed often is visible in the summary.
/// </summary>
internal static class VmBenchStats
{
    public static IReadOnlyList<VmBenchStat> Compute(IEnumerable<VmBenchSample> samples)
    {
        ArgumentNullException.ThrowIfNull(samples);

        var groups = samples
            .GroupBy(s => (s.Scenario, s.Os, s.Unit))
            .OrderBy(g => g.Key.Scenario)
            .ThenBy(g => g.Key.Os)
            .ThenBy(g => g.Key.Unit, StringComparer.Ordinal);

        var stats = new List<VmBenchStat>();
        foreach (var group in groups)
        {
            var failures = group.Count(s => s.Failed);
            var values = group.Where(s => !s.Failed).Select(s => s.Value).Order().ToArray();
            stats.Add(values.Length == 0
                ? new VmBenchStat(group.Key.Scenario, group.Key.Os, group.Key.Unit, 0, failures, 0, 0, 0, 0, 0)
                : new VmBenchStat(
                    group.Key.Scenario,
                    group.Key.Os,
                    group.Key.Unit,
                    values.Length,
                    failures,
                    Min: values[0],
                    Median: Median(values),
                    P95: NearestRankP95(values),
                    Max: values[^1],
                    Mean: values.Average()));
        }

        return stats;
    }

    /// <summary>Median of a sorted, non-empty array: the middle value, or the mean of the two middle values.</summary>
    private static double Median(double[] sorted)
    {
        var mid = sorted.Length / 2;
        return sorted.Length % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2.0;
    }

    /// <summary>Nearest-rank 95th percentile of a sorted, non-empty array.</summary>
    private static double NearestRankP95(double[] sorted)
    {
        var rank = (int)Math.Ceiling(0.95 * sorted.Length);
        return sorted[Math.Clamp(rank, 1, sorted.Length) - 1];
    }
}
