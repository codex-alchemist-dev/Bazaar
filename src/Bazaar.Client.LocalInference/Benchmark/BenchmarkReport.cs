using System.Text.Json;

namespace Bazaar.Client.LocalInference.Benchmark;

/// <summary>One corpus item run on one backend. <c>Error</c> is null when the run returned a result.</summary>
public sealed record BenchmarkRecord(string ItemId, BackendTier Tier, double ElapsedMs, bool Degraded, string? Error, bool GuardPassed);

/// <summary>Per-tier totals. Latency percentiles are null when the tier has no runs.</summary>
public sealed record TierSummary(BackendTier Tier, int Runs, int Errors, int Degraded, int GuardPassed, double? P50Ms, double? P95Ms);

/// <summary>
/// Recorded, not gated: the benchmark reports numbers and never judges them (doc 31, Testing).
/// Latency percentiles cover every run, including failed ones, so a timeout shows up in the tail.
/// </summary>
public sealed record BenchmarkReport(
    DateTimeOffset GeneratedAt,
    int Runs,
    int Errors,
    double ErrorRate,
    int GuardPassed,
    double? P50Ms,
    double? P95Ms,
    IReadOnlyList<TierSummary> Tiers,
    IReadOnlyList<BenchmarkRecord> Records)
{
    /// <summary>Builds the report from raw records. Pure: the timestamp is passed in.</summary>
    public static BenchmarkReport From(IReadOnlyList<BenchmarkRecord> records, DateTimeOffset generatedAt)
    {
        ArgumentNullException.ThrowIfNull(records);

        var latencies = records.Select(record => record.ElapsedMs).ToArray();
        var tiers = records
            .GroupBy(record => record.Tier)
            .Select(group => Summarize(group.Key, group.ToList()))
            .ToList();

        var errors = records.Count(record => record.Error is not null);
        return new BenchmarkReport(
            generatedAt,
            records.Count,
            errors,
            records.Count == 0 ? 0 : errors / (double)records.Count,
            records.Count(record => record.GuardPassed),
            BenchmarkStatistics.NearestRankPercentile(latencies, 50),
            BenchmarkStatistics.NearestRankPercentile(latencies, 95),
            tiers,
            records);
    }

    /// <summary>Serializes the report as indented JSON (camelCase, tiers as names).</summary>
    public string ToJson() => JsonSerializer.Serialize(this, BenchmarkJsonContext.Default.BenchmarkReport);

    /// <summary>Reads a report written by <see cref="ToJson"/>.</summary>
    public static BenchmarkReport FromJson(string json)
    {
        ArgumentNullException.ThrowIfNull(json);

        return JsonSerializer.Deserialize(json, BenchmarkJsonContext.Default.BenchmarkReport)
            ?? throw new JsonException("The benchmark report is empty.");
    }

    private static TierSummary Summarize(BackendTier tier, List<BenchmarkRecord> records)
    {
        var latencies = records.Select(record => record.ElapsedMs).ToArray();
        return new TierSummary(
            tier,
            records.Count,
            records.Count(record => record.Error is not null),
            records.Count(record => record.Degraded),
            records.Count(record => record.GuardPassed),
            BenchmarkStatistics.NearestRankPercentile(latencies, 50),
            BenchmarkStatistics.NearestRankPercentile(latencies, 95));
    }
}
