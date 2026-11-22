using Bazaar.Server.Knowledge.Metrics;
using Microsoft.Extensions.Time.Testing;

namespace Bazaar.Server.Health.Tests;

public class ProjectHealthServiceTests
{
    private static readonly DateOnly _today = new(2026, 11, 22);
    private const string P = "alpha";

    private static (ProjectHealthService Service, InMemoryMetricStore Store) Make(ProjectHealthSettings? settings = null)
    {
        var store = new InMemoryMetricStore();
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 11, 22, 12, 0, 0, TimeSpan.Zero));
        return (new ProjectHealthService(store, clock, settings), store);
    }

    private static MetricSample S(string metric, int daysAgo, double value, string dim = "", string src = "r") =>
        new(P, metric, dim, _today.AddDays(-daysAgo), value, src);

    [Fact]
    public async Task Nothing_recorded_is_no_data_not_healthy()
    {
        var (svc, _) = Make();
        var r = await svc.GetAsync(P, TestContext.Current.CancellationToken);

        Assert.Equal(ProjectHealthStatus.NoData, r.Status);
        Assert.Null(r.Build.PassRate);
        Assert.Null(r.TechDebt.RevertsInWindow);
        Assert.Null(r.TechDebt.AnalyzerWarnings);
        Assert.All(r.Morale, m => Assert.Null(m.Trend));
    }

    [Fact]
    public async Task Missing_ci_and_reverts_are_gaps_not_zeros()
    {
        var (svc, store) = Make();
        await store.UpsertAsync([S(HealthMetricNames.AnalyzerWarnings, 0, 12)], TestContext.Current.CancellationToken);
        var r = await svc.GetAsync(P, TestContext.Current.CancellationToken);

        Assert.Equal(ProjectHealthStatus.Healthy, r.Status);
        Assert.Null(r.Build.PassRate);
        Assert.Null(r.TechDebt.RevertsInWindow);
        Assert.Equal(12, r.TechDebt.AnalyzerWarnings!.Latest);
        Assert.Null(r.TechDebt.AnalyzerWarnings.Previous);
        Assert.Equal(TrendDirection.Unknown, r.TechDebt.AnalyzerWarnings.Direction);
    }

    [Fact]
    public async Task Coverage_drop_within_one_toolchain_raises_an_alert_and_toolchains_stay_separate()
    {
        var (svc, store) = Make();
        await store.UpsertAsync([
            S(MetricNames.CoverageLinePercent, 1, 80, "dotnet", "a"),
            S(MetricNames.CoverageLinePercent, 0, 75, "dotnet", "b"),
            S(MetricNames.CoverageLinePercent, 0, 40, "luau", "c"),
        ], TestContext.Current.CancellationToken);
        var r = await svc.GetAsync(P, TestContext.Current.CancellationToken);

        Assert.Equal(ProjectHealthStatus.Alert, r.Status);
        Assert.Contains(r.Alerts, a => a.Kind == "coverage_drop" && a.Message.Contains("dotnet"));
        Assert.DoesNotContain(r.Alerts, a => a.Message.Contains("luau"));
        var dotnet = r.Coverage.Single(c => c.Toolchain == "dotnet");
        Assert.Equal(-5, dotnet.Trend.Change);
        Assert.Equal(TrendDirection.Falling, dotnet.Trend.Direction);
    }

    [Fact]
    public async Task Build_failure_streak_raises_an_alert_at_the_configured_length()
    {
        var (svc, store) = Make(new ProjectHealthSettings { BuildFailureStreak = 2 });
        await store.UpsertAsync([
            S(MetricNames.CiRunsTotal, 1, 4, src: "a"), S(MetricNames.CiRunsPassed, 1, 3, src: "a"),
            S(MetricNames.CiRunsTotal, 0, 2, src: "b"), S(MetricNames.CiRunsPassed, 0, 0, src: "b"),
        ], TestContext.Current.CancellationToken);
        var r = await svc.GetAsync(P, TestContext.Current.CancellationToken);

        Assert.Contains(r.Alerts, a => a.Kind == "build_failure_streak");
        Assert.Equal(6, r.Build.Runs);
        Assert.Equal(3, r.Build.Passed);
        Assert.Equal(0.5, r.Build.PassRate);
    }

    [Fact]
    public async Task Tech_debt_sums_toolchains_per_day_and_totals_reverts()
    {
        var (svc, store) = Make();
        await store.UpsertAsync([
            S(HealthMetricNames.AnalyzerWarnings, 1, 10, "dotnet"), S(HealthMetricNames.AnalyzerWarnings, 1, 5, "luau"),
            S(HealthMetricNames.AnalyzerWarnings, 0, 8, "dotnet"), S(HealthMetricNames.AnalyzerWarnings, 0, 4, "luau"),
            S(HealthMetricNames.PackageVulnerabilities, 0, 2),
            S(HealthMetricNames.Reverts, 3, 1, src: "x"), S(HealthMetricNames.Reverts, 0, 2, src: "y"),
        ], TestContext.Current.CancellationToken);
        var r = await svc.GetAsync(P, TestContext.Current.CancellationToken);

        Assert.Equal(12, r.TechDebt.AnalyzerWarnings!.Latest);
        Assert.Equal(15, r.TechDebt.AnalyzerWarnings.Previous);
        Assert.Equal(TrendDirection.Falling, r.TechDebt.AnalyzerWarnings.Direction);
        Assert.Equal(2, r.TechDebt.PackageVulnerabilities!.Latest);
        Assert.Equal(3, r.TechDebt.RevertsInWindow);
    }

    [Fact]
    public async Task Samples_outside_the_window_are_ignored()
    {
        var (svc, store) = Make(new ProjectHealthSettings { WindowDays = 7 });
        await store.UpsertAsync([S(HealthMetricNames.Reverts, 20, 9)], TestContext.Current.CancellationToken);
        var r = await svc.GetAsync(P, TestContext.Current.CancellationToken);

        Assert.Equal(ProjectHealthStatus.NoData, r.Status);
    }

    [Fact]
    public async Task Morale_reports_only_the_configured_project_level_signals_and_ignores_unknown_names()
    {
        var (svc, store) = Make(new ProjectHealthSettings { MoraleSignals = [MoraleSignals.BlockedShare, "sentiment", "nope"] });
        await store.UpsertAsync([
            S(HealthMetricNames.BlockedShare, 1, 0.1, src: "a"), S(HealthMetricNames.BlockedShare, 1, 0.3, src: "b"),
            S(HealthMetricNames.BlockedShare, 0, 0.4),
            S(HealthMetricNames.ReopenedTasks, 0, 5),
        ], TestContext.Current.CancellationToken);
        var r = await svc.GetAsync(P, TestContext.Current.CancellationToken);

        var m = Assert.Single(r.Morale);
        Assert.Equal(MoraleSignals.BlockedShare, m.Signal);
        Assert.Equal(0.4, m.Trend!.Latest);
        Assert.Equal(0.2, m.Trend.Previous!.Value, 6); // a ratio is averaged, not summed
        Assert.Equal(TrendDirection.Rising, m.Trend.Direction);
    }

    [Fact]
    public async Task Default_morale_signals_are_the_three_documented_ones()
    {
        var (svc, store) = Make();
        await store.UpsertAsync([S(HealthMetricNames.ReopenedTasks, 0, 2), S(HealthMetricNames.VoteReversals, 0, 1)], TestContext.Current.CancellationToken);
        var r = await svc.GetAsync(P, TestContext.Current.CancellationToken);

        Assert.Equal(["blocked_share", "reopened_tasks", "vote_reversals"], r.Morale.Select(m => m.Signal));
        Assert.Equal(ProjectHealthStatus.Healthy, r.Status);
    }

    [Fact]
    public async Task Same_samples_give_the_same_report()
    {
        var (svc, store) = Make();
        await store.UpsertAsync([S(MetricNames.CoverageLinePercent, 1, 80, "dotnet"), S(MetricNames.CoverageLinePercent, 0, 70, "dotnet", "z")], TestContext.Current.CancellationToken);

        var a = HealthDashboardCard.ToPaneModel(HealthDashboardCard.Build(await svc.GetAsync(P, TestContext.Current.CancellationToken)));
        var b = HealthDashboardCard.ToPaneModel(HealthDashboardCard.Build(await svc.GetAsync(P, TestContext.Current.CancellationToken)));
        Assert.Equal(a, b);
    }
}
