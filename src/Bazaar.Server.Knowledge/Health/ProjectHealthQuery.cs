using Bazaar.Server.Knowledge.Metrics;

namespace Bazaar.Server.Knowledge.Health;

/// <summary>Builds the project health panel from the metric store. Reads the clock only through <see cref="TimeProvider"/>.</summary>
public static class ProjectHealthQuery
{
    /// <summary>How many days, ending on the as-of day, the rules look at.</summary>
    public const int DefaultWindowDays = 30;

    public static async Task<ProjectHealthPanel> GetAsync(
        IMetricStore store,
        TimeProvider timeProvider,
        string projectId,
        HealthThresholds thresholds,
        int windowDays = DefaultWindowDays,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentNullException.ThrowIfNull(thresholds);
        ArgumentOutOfRangeException.ThrowIfLessThan(windowDays, 2);

        var asOf = DateOnly.FromDateTime(timeProvider.GetUtcNow().UtcDateTime);
        var fromDay = asOf.AddDays(1 - windowDays);

        var coverage = await store.QueryAsync(projectId, MetricNames.CoverageLinePercent, fromDay, asOf, cancellationToken);
        var runsTotal = await store.QueryAsync(projectId, MetricNames.CiRunsTotal, fromDay, asOf, cancellationToken);
        var runsPassed = await store.QueryAsync(projectId, MetricNames.CiRunsPassed, fromDay, asOf, cancellationToken);

        var reasons = new List<HealthReason>();
        reasons.AddRange(HealthRules.CoverageDrops(coverage, thresholds.CoverageDropPoints));
        reasons.AddRange(HealthRules.BuildFailureStreak([.. runsTotal, .. runsPassed], fromDay, asOf, thresholds.BuildFailureStreak));

        var hasData = coverage.Count > 0 || runsTotal.Count > 0 || runsPassed.Count > 0;
        var status = reasons.Count > 0 ? HealthStatus.Alert : hasData ? HealthStatus.Healthy : HealthStatus.NoData;
        return new ProjectHealthPanel(projectId, asOf, status, reasons);
    }
}
