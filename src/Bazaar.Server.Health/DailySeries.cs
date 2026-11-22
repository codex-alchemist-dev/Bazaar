using Bazaar.Server.Knowledge.Metrics;

namespace Bazaar.Server.Health;

/// <summary>One day's value of a metric after rollup.</summary>
internal readonly record struct DayValue(DateOnly Day, double Value);

/// <summary>
/// Daily rollup of one metric over all its samples and dimensions. Counts are summed, ratios are averaged. A day with no sample has
/// no entry: it is a gap, not a zero. Pure: the same samples always give the same series.
/// </summary>
internal static class DailySeries
{
    public static IReadOnlyList<DayValue> Build(string metric, IEnumerable<MetricSample> samples)
    {
        var count = HealthMetricNames.IsCount(metric);
        return samples
            .Where(s => s.Metric == metric)
            .GroupBy(s => s.Bucket)
            .OrderBy(g => g.Key)
            .Select(g => new DayValue(g.Key, count ? g.Sum(s => s.Value) : g.Average(s => s.Value)))
            .ToList();
    }

    /// <summary>Newest recorded day and the one before it, or null with no data.</summary>
    public static MetricTrend? Trend(IReadOnlyList<DayValue> series) =>
        series.Count == 0
            ? null
            : new MetricTrend(series[^1].Value, series[^1].Day, series.Count > 1 ? series[^2].Value : null);

    /// <summary>Sum over the window, or null when no day was recorded.</summary>
    public static double? Total(IReadOnlyList<DayValue> series) => series.Count == 0 ? null : series.Sum(d => d.Value);
}
