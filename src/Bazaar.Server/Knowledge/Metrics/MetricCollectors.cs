using System.Globalization;
using System.Xml.Linq;

namespace Bazaar.Server.Knowledge.Metrics;

/// <summary>Turns CI run facts into per-day samples. Pure: no storage and no clock.</summary>
public static class CiRunCollector
{
    /// <summary>
    /// Emits <c>ci.runs_total</c> and <c>ci.runs_passed</c> for each UTC day that has at least one completed run. Days without runs
    /// produce nothing. Within one batch, a run id that repeats is counted once. Across batches, the day sample is replaced with the recomputed total.
    /// </summary>
    public static IReadOnlyList<MetricSample> Collect(string projectId, IEnumerable<CiRunFact> runs)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentNullException.ThrowIfNull(runs);

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var totals = new SortedDictionary<DateOnly, (int Total, int Passed)>();
        foreach (var run in runs.OrderBy(r => r.CompletedAt).ThenBy(r => r.RunId, StringComparer.Ordinal))
        {
            if (!seen.Add(run.RunId))
                continue;

            var day = DateOnly.FromDateTime(run.CompletedAt.UtcDateTime);
            totals.TryGetValue(day, out var counts);
            totals[day] = (counts.Total + 1, counts.Passed + (run.Passed ? 1 : 0));
        }

        var samples = new List<MetricSample>();
        foreach (var (day, counts) in totals)
        {
            var source = $"ci:{day:yyyy-MM-dd}";
            samples.Add(new MetricSample(projectId, MetricNames.CiRunsTotal, string.Empty, day, counts.Total, source));
            samples.Add(new MetricSample(projectId, MetricNames.CiRunsPassed, string.Empty, day, counts.Passed, source));
        }

        return samples;
    }
}

/// <summary>Reads a Cobertura XML report into one line-coverage sample.</summary>
public static class CoverageCollector
{
    /// <summary>
    /// Reads the root <c>line-rate</c> attribute. The toolchain is the dimension, so coverage is only compared within one toolchain
    /// (doc 25 health rules). Returns null when the report has no usable line rate; the caller records a gap.
    /// </summary>
    public static MetricSample? Parse(string projectId, string toolchain, DateOnly day, string cobertura, string sourceRef)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(toolchain);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceRef);

        var root = XDocument.Parse(cobertura).Root;
        var raw = root?.Attribute("line-rate")?.Value;
        if (raw is null || !double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var rate)
            || rate < 0 || rate > 1)
        {
            return null;
        }

        return new MetricSample(projectId, MetricNames.CoverageLinePercent, toolchain, day, Math.Round(rate * 100, 2), sourceRef);
    }
}
