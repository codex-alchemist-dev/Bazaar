using Bazaar.Server.VmBench.Abstractions;

namespace Bazaar.Server.VmBench;

/// <summary>
/// One scenario and guest OS whose median got worse than the baseline by more than the tolerance.
/// <see cref="RatioChange"/> is <c>CurrentMedian / BaselineMedian</c>, so 1.20 means 20% worse for a higher-is-worse scenario.
/// </summary>
public sealed record VmBenchRegression(
    VmBenchScenario Scenario,
    BenchGuestOs Os,
    string Unit,
    double BaselineMedian,
    double CurrentMedian,
    double RatioChange);

/// <summary>Compares two benchmark runs and reports the medians that regressed.</summary>
public static class VmBenchComparison
{
    /// <summary>
    /// Returns the regressions of <paramref name="current"/> against <paramref name="baseline"/>. Time and RAM scenarios are worse when
    /// higher; ShareThroughput is worse when lower. A pair is skipped when either side is missing, has no successful samples, or has a
    /// non-positive baseline median (the ratio is undefined). Improvements are not reported.
    /// </summary>
    public static IReadOnlyList<VmBenchRegression> Compare(VmBenchReport baseline, VmBenchReport current, double tolerance = 0.10)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(current);
        ArgumentOutOfRangeException.ThrowIfNegative(tolerance);

        var baseByKey = new Dictionary<(VmBenchScenario, BenchGuestOs), VmBenchStat>();
        foreach (var stat in baseline.Stats)
        {
            baseByKey[(stat.Scenario, stat.Os)] = stat;
        }

        var regressions = new List<VmBenchRegression>();
        foreach (var stat in current.Stats.OrderBy(s => s.Scenario).ThenBy(s => s.Os))
        {
            if (!baseByKey.TryGetValue((stat.Scenario, stat.Os), out var before))
            {
                continue;
            }

            if (before.Count == 0 || stat.Count == 0 || before.Median <= 0)
            {
                continue;
            }

            var ratio = stat.Median / before.Median;
            var worse = LowerIsWorse(stat.Scenario)
                ? ratio < 1 - tolerance
                : ratio > 1 + tolerance;
            if (!worse)
            {
                continue;
            }

            regressions.Add(new VmBenchRegression(
                stat.Scenario, stat.Os, stat.Unit, before.Median, stat.Median, ratio));
        }

        return regressions;
    }

    // Throughput is the only scenario where a lower number is the regression; every other measured scenario is a time or RAM cost.
    private static bool LowerIsWorse(VmBenchScenario scenario) => scenario == VmBenchScenario.ShareThroughput;
}
