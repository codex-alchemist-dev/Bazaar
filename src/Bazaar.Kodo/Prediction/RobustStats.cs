namespace Bazaar.Kodo.Prediction;

/// <summary>Small deterministic helpers for medians and percentiles. Callers pass finite values only.</summary>
internal static class RobustStats
{
    /// <summary>Median of an already sorted, non-empty span (mean of the middle two for an even count).</summary>
    public static double MedianSorted(ReadOnlySpan<double> sorted)
    {
        int n = sorted.Length;
        int mid = n / 2;
        return n % 2 == 1 ? sorted[mid] : (sorted[mid - 1] / 2) + (sorted[mid] / 2);
    }

    /// <summary>Nearest-rank percentile of an already sorted, non-empty span. <paramref name="p"/> is in (0, 1].</summary>
    public static double PercentileSorted(ReadOnlySpan<double> sorted, double p)
    {
        int rank = (int)Math.Ceiling(p * sorted.Length) - 1;
        return sorted[Math.Clamp(rank, 0, sorted.Length - 1)];
    }

    public static double Median(double[] values)
    {
        Array.Sort(values);
        return MedianSorted(values);
    }

    /// <summary>The time <paramref name="span"/> before <paramref name="time"/>, clamped to the smallest representable time.</summary>
    public static DateTimeOffset Before(DateTimeOffset time, TimeSpan span) =>
        time.UtcTicks - DateTime.MinValue.Ticks < span.Ticks ? DateTimeOffset.MinValue : time - span;
}
