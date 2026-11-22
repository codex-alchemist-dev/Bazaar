using Bazaar.Server.CodingBench.Abstractions;

namespace Bazaar.Server.CodingBench;

/// <summary>
/// Aggregates scored runs per model and edit format, and picks the best edit format per model
/// (docs/design/17-coding-agent-harness.md, "Edit formats and benchmark").
/// </summary>
public static class CodingBenchSummarizer
{
    /// <summary>Fewer runs than this in a format and the format is not recommended.</summary>
    private const int _minRunsForRecommendation = 3;

    /// <summary>Groups runs by (model, format), ordered by model (ordinal) then format (enum order).</summary>
    public static IReadOnlyList<CodingBenchSummary> Summarize(IReadOnlyList<CodingRunResult> results)
    {
        ArgumentNullException.ThrowIfNull(results);

        return results
            .GroupBy(r => (r.Request.Model, r.Request.Format))
            .OrderBy(g => g.Key.Model, StringComparer.Ordinal)
            .ThenBy(g => g.Key.Format)
            .Select(g => Aggregate(g.Key.Model, g.Key.Format, g.ToList()))
            .ToList();
    }

    /// <summary>
    /// The best format per model by pass rate, then format compliance (higher), mean tokens (lower),
    /// fake-success rate (lower), then enum order. Formats with fewer than 3 runs are ignored; a model
    /// with no qualifying format maps to <see cref="BenchEditFormat.WholeFile"/>.
    /// </summary>
    public static IReadOnlyDictionary<string, BenchEditFormat> RecommendFormat(IReadOnlyList<CodingBenchSummary> summaries)
    {
        ArgumentNullException.ThrowIfNull(summaries);

        var models = summaries.Select(s => s.Model).Distinct(StringComparer.Ordinal).OrderBy(m => m, StringComparer.Ordinal);
        var recommendations = new Dictionary<string, BenchEditFormat>(StringComparer.Ordinal);
        foreach (var model in models)
        {
            var best = summaries
                .Where(s => s.Model == model && s.Runs >= _minRunsForRecommendation)
                .OrderByDescending(s => s.PassRate)
                .ThenByDescending(s => s.FormatCompliance)
                .ThenBy(s => s.MeanTokensPerTask)
                .ThenBy(s => s.FakeSuccessRate)
                .ThenBy(s => s.Format)
                .FirstOrDefault();
            recommendations[model] = best?.Format ?? BenchEditFormat.WholeFile;
        }

        return recommendations;
    }

    private static CodingBenchSummary Aggregate(string model, BenchEditFormat format, List<CodingRunResult> runs)
    {
        var correct = runs.Count(r => r.Correct);
        var attempted = runs.Sum(r => (long)r.Outcome.EditsAttempted);
        var malformed = runs.Sum(r => (long)r.Outcome.EditsMalformed);
        var claimed = runs.Count(r => r.Outcome.ClaimedSuccess);
        var fake = runs.Count(r => r.FakeSuccess && r.Outcome.ClaimedSuccess);
        var costs = runs.Where(r => r.Outcome.CostUsd.HasValue).Select(r => r.Outcome.CostUsd!.Value).ToList();

        return new CodingBenchSummary(
            Model: model,
            Format: format,
            Runs: runs.Count,
            PassRate: (double)correct / runs.Count,
            FormatCompliance: attempted == 0 ? 1.0 : 1.0 - (double)malformed / attempted,
            MalformedEditRate: attempted == 0 ? 0.0 : (double)malformed / attempted,
            FakeSuccessRate: claimed == 0 ? 0.0 : (double)fake / claimed,
            MeanTokensPerTask: runs.Average(r => (double)(r.Outcome.InputTokens + r.Outcome.OutputTokens)),
            MeanTurns: runs.Average(r => (double)r.Outcome.Turns),
            TotalCostUsd: costs.Count == 0 ? null : costs.Sum());
    }
}
