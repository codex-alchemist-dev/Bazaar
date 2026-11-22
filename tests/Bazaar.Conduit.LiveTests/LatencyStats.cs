namespace Bazaar.Conduit.LiveTests;

/// <summary>Summary of round-trip times, in milliseconds. Pure arithmetic, so it is tested offline.</summary>
public sealed record LatencyStats(int Count, double MinMs, double P50Ms, double P95Ms, double MaxMs, double MeanMs)
{
    /// <summary>Computes the summary. Percentiles use the nearest-rank method. Throws on an empty sample.</summary>
    public static LatencyStats From(IReadOnlyCollection<TimeSpan> samples)
    {
        ArgumentNullException.ThrowIfNull(samples);
        if (samples.Count == 0)
        {
            throw new ArgumentException("At least one sample is required.", nameof(samples));
        }

        var sorted = samples.Select(s => s.TotalMilliseconds).Order().ToArray();
        return new LatencyStats(sorted.Length, sorted[0], Rank(sorted, 0.50), Rank(sorted, 0.95), sorted[^1], sorted.Average());
    }

    private static double Rank(double[] sorted, double percentile)
    {
        var index = (int)Math.Ceiling(percentile * sorted.Length) - 1;
        return sorted[Math.Clamp(index, 0, sorted.Length - 1)];
    }
}
