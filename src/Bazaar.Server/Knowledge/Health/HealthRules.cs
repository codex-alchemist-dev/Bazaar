using System.Globalization;
using Bazaar.Server.Knowledge.Metrics;

namespace Bazaar.Server.Knowledge.Health;

/// <summary>Which health rule raised a reason.</summary>
public enum HealthReasonKind
{
    CoverageDrop,
    BuildFailureStreak,
}

/// <summary>One reason a project is flagged. <see cref="Message"/> is for people; the kind is for code.</summary>
public sealed record HealthReason(HealthReasonKind Kind, string Message);

/// <summary>
/// The two project health thresholds (doc 25 <c>health.coverage_drop_points</c> and <c>health.build_failure_streak</c>).
/// The caller reads them from settings; the rules never read settings themselves.
/// </summary>
public sealed record HealthThresholds
{
    public HealthThresholds(int coverageDropPoints, int buildFailureStreak)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(coverageDropPoints);
        ArgumentOutOfRangeException.ThrowIfLessThan(buildFailureStreak, 1);

        CoverageDropPoints = coverageDropPoints;
        BuildFailureStreak = buildFailureStreak;
    }

    /// <summary>Coverage drop, in percentage points, that raises an alert.</summary>
    public int CoverageDropPoints { get; }

    /// <summary>Consecutive failing CI days that raise an alert.</summary>
    public int BuildFailureStreak { get; }

    /// <summary>The shipped defaults from <see cref="KnowledgeSettings"/>.</summary>
    public static HealthThresholds Defaults { get; } = new(
        KnowledgeSettings.HealthCoverageDropPoints.Default,
        KnowledgeSettings.HealthBuildFailureStreak.Default);
}

/// <summary>
/// Health rules over metric samples (doc 25 Health and Analytics, WP2). Pure: the same samples and thresholds always give the
/// same reasons, and no clock or storage is read. Every rule works on the daily rollup, so a day with several samples counts once.
/// </summary>
public static class HealthRules
{
    /// <summary>
    /// Flags a toolchain when its latest recorded line coverage is at least <paramref name="dropPoints"/> below the previous recorded
    /// day. "Previous recorded day" means the nearest earlier day that has a value; a day with no sample is a gap, is skipped, and is
    /// never read as zero. Coverage is compared only within one toolchain (the dimension), never across toolchains. A rise or no change
    /// never raises an alert, even when <paramref name="dropPoints"/> is 0.
    /// </summary>
    public static IReadOnlyList<HealthReason> CoverageDrops(IEnumerable<MetricSample> samples, int dropPoints)
    {
        ArgumentNullException.ThrowIfNull(samples);
        ArgumentOutOfRangeException.ThrowIfNegative(dropPoints);

        var coverage = MetricRollup.Daily(samples.Where(s => s.Metric == MetricNames.CoverageLinePercent));
        var reasons = new List<HealthReason>();

        foreach (var toolchain in coverage.GroupBy(r => r.Dimension, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var rows = toolchain.OrderBy(r => r.Bucket).ToList();
            if (rows.Count < 2)
                continue;

            var latest = rows[^1];
            var previous = rows[^2];
            var drop = Math.Round(previous.Value - latest.Value, 2);
            if (drop > 0 && drop >= dropPoints)
            {
                reasons.Add(new HealthReason(
                    HealthReasonKind.CoverageDrop,
                    $"Line coverage for {toolchain.Key} fell {Format(drop)} points to {Format(latest.Value)}% on {latest.Bucket:yyyy-MM-dd} " +
                    $"(was {Format(previous.Value)}% on {previous.Bucket:yyyy-MM-dd}); alert at {dropPoints} points."));
            }
        }

        return reasons;
    }

    /// <summary>
    /// Flags when the last <paramref name="streakThreshold"/> or more consecutive days in <paramref name="fromDay"/>..<paramref name="toDay"/>
    /// had at least one failed CI run. Walks back from <paramref name="toDay"/>. A day with failures counts toward the streak. A day with
    /// every run passing ends the streak. A day with no CI sample, or with zero runs, is a gap: it is neither a failure nor a pass, so it
    /// neither counts nor resets the streak. Gaps between counted failures are bridged, and the message says how many were bridged.
    /// Gaps after the newest failure, or older than the oldest counted failure, are not reported as bridged.
    /// </summary>
    public static IReadOnlyList<HealthReason> BuildFailureStreak(IEnumerable<MetricSample> samples, DateOnly fromDay, DateOnly toDay, int streakThreshold)
    {
        ArgumentNullException.ThrowIfNull(samples);
        ArgumentOutOfRangeException.ThrowIfLessThan(streakThreshold, 1);
        if (fromDay > toDay)
            throw new ArgumentException("fromDay must not be after toDay.", nameof(fromDay));

        var rollup = MetricRollup.Daily(samples.Where(s => s.Metric is MetricNames.CiRunsTotal or MetricNames.CiRunsPassed));
        var totals = rollup.Where(r => r.Metric == MetricNames.CiRunsTotal).ToDictionary(r => r.Bucket, r => r.Value);
        var passed = rollup.Where(r => r.Metric == MetricNames.CiRunsPassed).ToDictionary(r => r.Bucket, r => r.Value);

        var streak = 0;
        var bridged = 0;
        var pendingGaps = 0;
        for (var day = toDay; day >= fromDay; day = day.AddDays(-1))
        {
            if (!totals.TryGetValue(day, out var total) || total <= 0 || !passed.TryGetValue(day, out var passedRuns))
            {
                // Gap: nothing recorded for the day. It is held as pending: it is bridged only when a further failure follows it.
                pendingGaps++;
                continue;
            }

            if (total - passedRuns <= 0)
                break;

            // Gaps only count as bridged when they sit between two failures, not before the newest one.
            if (streak > 0)
                bridged += pendingGaps;
            pendingGaps = 0;
            streak++;
        }

        if (streak < streakThreshold)
            return [];

        var gapNote = bridged > 0
            ? $" {bridged} day(s) with no CI run inside the streak were skipped, not counted as failures or passes."
            : string.Empty;
        return
        [
            new HealthReason(
                HealthReasonKind.BuildFailureStreak,
                $"{streak} consecutive CI days with failed runs, up to {toDay:yyyy-MM-dd}; alert at {streakThreshold}.{gapNote}"),
        ];
    }

    private static string Format(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);
}
