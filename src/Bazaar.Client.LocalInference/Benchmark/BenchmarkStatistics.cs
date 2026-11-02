namespace Bazaar.Client.LocalInference.Benchmark;

/// <summary>Descriptive statistics for benchmark latencies.</summary>
public static class BenchmarkStatistics
{
    /// <summary>
    /// Nearest-rank percentile: the value at rank ceil(percentile * n / 100) in ascending order.
    /// Returns null for an empty list. <paramref name="percentile"/> must be in (0, 100].
    /// </summary>
    public static double? NearestRankPercentile(IReadOnlyList<double> values, double percentile)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (!(percentile > 0 && percentile <= 100))
        {
            throw new ArgumentOutOfRangeException(nameof(percentile), percentile, "Percentile must be in (0, 100].");
        }

        if (values.Count == 0)
        {
            return null;
        }

        var sorted = values.Order().ToArray();
        var rank = (int)Math.Ceiling(percentile * sorted.Length / 100.0);
        return sorted[Math.Clamp(rank - 1, 0, sorted.Length - 1)];
    }
}
