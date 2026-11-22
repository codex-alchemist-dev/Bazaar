using Bazaar.Server.Knowledge.Health;
using Bazaar.Server.Knowledge.Metrics;

namespace Bazaar.Server.Health;

/// <summary>
/// Builds the project health report (doc 25 Health and Analytics, WP2). Alerts come from the existing <see cref="HealthRules"/>, so
/// the two rules (coverage drop, build failure streak) stay defined in one place. Tech debt and morale are plain readings of
/// project-level metrics. This type reads metric samples only: it has no path to reputation, the Work-Life schedule or votes (doc 25
/// Boundaries; a build-time check in the tests enforces it). The clock is read through <see cref="TimeProvider"/>.
/// </summary>
public sealed class ProjectHealthService(IMetricStore store, TimeProvider timeProvider, ProjectHealthSettings? settings = null) : IProjectHealthService
{
    private readonly ProjectHealthSettings _settings = settings ?? ProjectHealthSettings.Defaults;

    public async Task<ProjectHealthReport> GetAsync(string projectId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentOutOfRangeException.ThrowIfLessThan(_settings.WindowDays, 2);

        var asOf = DateOnly.FromDateTime(timeProvider.GetUtcNow().UtcDateTime);
        var from = asOf.AddDays(1 - _settings.WindowDays);

        async Task<IReadOnlyList<MetricSample>> Load(string metric) =>
            await store.QueryAsync(projectId, metric, from, asOf, cancellationToken);

        var coverage = await Load(MetricNames.CoverageLinePercent);
        var runsTotal = await Load(MetricNames.CiRunsTotal);
        var runsPassed = await Load(MetricNames.CiRunsPassed);
        var warnings = await Load(HealthMetricNames.AnalyzerWarnings);
        var vulnerabilities = await Load(HealthMetricNames.PackageVulnerabilities);
        var reverts = await Load(HealthMetricNames.Reverts);

        var alerts = new List<HealthAlert>();
        foreach (var reason in HealthRules.CoverageDrops(coverage, _settings.CoverageDropPoints))
            alerts.Add(new HealthAlert("coverage_drop", reason.Message));
        foreach (var reason in HealthRules.BuildFailureStreak([.. runsTotal, .. runsPassed], from, asOf, _settings.BuildFailureStreak))
            alerts.Add(new HealthAlert("build_failure_streak", reason.Message));

        var morale = new List<MoraleReading>();
        var hasMoraleData = false;
        foreach (var signal in _settings.MoraleSignals.Distinct(StringComparer.Ordinal))
        {
            var metric = MoraleSignals.MetricFor(signal);
            if (metric is null)
                continue; // A name this version does not know is ignored, never guessed at.

            var series = DailySeries.Build(metric, await Load(metric));
            hasMoraleData |= series.Count > 0;
            morale.Add(new MoraleReading(signal, DailySeries.Trend(series), DailySeries.Total(series)));
        }

        var hasData = coverage.Count + runsTotal.Count + runsPassed.Count + warnings.Count + vulnerabilities.Count + reverts.Count > 0
            || hasMoraleData;
        var status = alerts.Count > 0 ? ProjectHealthStatus.Alert : hasData ? ProjectHealthStatus.Healthy : ProjectHealthStatus.NoData;

        return new ProjectHealthReport(
            projectId,
            asOf,
            _settings.WindowDays,
            status,
            alerts,
            BuildCoverage(coverage),
            BuildBuild(runsTotal, runsPassed),
            new TechDebtReading(
                DailySeries.Trend(DailySeries.Build(HealthMetricNames.AnalyzerWarnings, warnings)),
                DailySeries.Trend(DailySeries.Build(HealthMetricNames.PackageVulnerabilities, vulnerabilities)),
                DailySeries.Total(DailySeries.Build(HealthMetricNames.Reverts, reverts))),
            morale);
    }

    private static List<CoverageReading> BuildCoverage(IReadOnlyList<MetricSample> samples) =>
        MetricRollup.Daily(samples.Where(s => s.Metric == MetricNames.CoverageLinePercent))
            .GroupBy(r => r.Dimension, StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g =>
            {
                var rows = g.OrderBy(r => r.Bucket).ToList();
                var trend = new MetricTrend(rows[^1].Value, rows[^1].Bucket, rows.Count > 1 ? rows[^2].Value : null);
                return new CoverageReading(g.Key, trend);
            })
            .ToList();

    private static BuildReading BuildBuild(IReadOnlyList<MetricSample> total, IReadOnlyList<MetricSample> passed)
    {
        var runs = (int)total.Sum(s => s.Value);
        var ok = (int)passed.Sum(s => s.Value);
        return new BuildReading(runs, ok, runs > 0 ? Math.Round((double)ok / runs, 4) : null);
    }
}
