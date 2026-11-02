namespace Bazaar.Server.Knowledge.Metrics;

/// <summary>
/// One day-level rollup of a metric, recomputable from its samples. For counts the rollup is the sum; for rates it is the mean,
/// weighted by the number of contributing samples.
/// </summary>
public sealed record MetricRollupRow(string ProjectId, string Metric, string Dimension, DateOnly Bucket, double Value, int Samples);

/// <summary>Computes daily rollups over stored samples. Pure: the same input always gives the same rows.</summary>
public static class MetricRollup
{
    /// <summary>Metrics whose daily value is a count and is summed. Everything else is averaged.</summary>
    private static readonly HashSet<string> CountMetrics = new(StringComparer.Ordinal) { MetricNames.CiRunsTotal, MetricNames.CiRunsPassed };

    public static IReadOnlyList<MetricRollupRow> Daily(IEnumerable<MetricSample> samples)
    {
        ArgumentNullException.ThrowIfNull(samples);

        return samples
            .GroupBy(s => (s.ProjectId, s.Metric, s.Dimension, s.Bucket))
            .OrderBy(g => g.Key.ProjectId, StringComparer.Ordinal)
            .ThenBy(g => g.Key.Metric, StringComparer.Ordinal)
            .ThenBy(g => g.Key.Dimension, StringComparer.Ordinal)
            .ThenBy(g => g.Key.Bucket)
            .Select(g =>
            {
                var values = g.Select(s => s.Value).ToList();
                var value = CountMetrics.Contains(g.Key.Metric) ? values.Sum() : values.Average();
                return new MetricRollupRow(g.Key.ProjectId, g.Key.Metric, g.Key.Dimension, g.Key.Bucket, value, values.Count);
            })
            .ToList();
    }
}
