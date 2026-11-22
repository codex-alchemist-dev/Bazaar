using Bazaar.Server.VmDisplay.Abstractions;

namespace Bazaar.Server.VmBench.Display;

/// <summary>Result builders and statistics shared by the scenarios.</summary>
internal static class DisplayBenchSupport
{
    public const double BytesPerMiB = 1024d * 1024d;
    private static readonly IReadOnlyList<DisplayBenchMetric> _none = [];

    public static DisplayBenchResult NotRun(DisplayBenchScenario scenario, string variant, string unit, string reason) =>
        new(scenario, variant, DisplayBenchOutcome.NotRun, null, unit, null, null, _none, reason);

    public static DisplayBenchResult Failed(DisplayBenchScenario scenario, string variant, string unit, string reason) =>
        new(scenario, variant, DisplayBenchOutcome.Failed, null, unit, null, null, _none, reason);

    /// <summary>A pass when <paramref name="value"/> is at most <paramref name="max"/>; the edge itself passes.</summary>
    public static DisplayBenchOutcome AtMost(double value, double max) => value <= max ? DisplayBenchOutcome.Passed : DisplayBenchOutcome.Failed;

    /// <summary>True when the failure is the caller's cancellation, which must propagate and not become a verdict.</summary>
    public static bool IsCallerCancellation(Exception ex, CancellationToken ct) => ex is OperationCanceledException && ct.IsCancellationRequested;

    public static string Describe(Exception ex) => $"{ex.GetType().Name}: {ex.Message}";

    public static string SourceName(DisplaySourceKind source) => source.ToString().ToLowerInvariant();

    /// <summary>Count, failures, min, median, nearest-rank p95, max and mean. Failed attempts count only as failures.</summary>
    public static DisplayBenchStat Stat(IEnumerable<double> values, int failures)
    {
        var sorted = values.Order().ToArray();
        if (sorted.Length == 0)
        {
            return new DisplayBenchStat(0, failures, 0, 0, 0, 0, 0);
        }

        var mid = sorted.Length / 2;
        var median = sorted.Length % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2.0;
        var rank = Math.Clamp((int)Math.Ceiling(0.95 * sorted.Length), 1, sorted.Length);
        return new DisplayBenchStat(sorted.Length, failures, sorted[0], median, sorted[rank - 1], sorted[^1], sorted.Average());
    }
}
