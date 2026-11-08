using Bazaar.Server.Knowledge.Health;
using Bazaar.Server.Knowledge.Metrics;
using Microsoft.Extensions.Time.Testing;

namespace Bazaar.Server.Tests.Knowledge.Health;

public sealed class ProjectHealthQueryTests
{
    private static readonly DateOnly _asOf = new(2026, 11, 8);
    private static readonly HealthThresholds _thresholds = new(2, 3);

    private static FakeTimeProvider Clock() => new(new DateTimeOffset(2026, 11, 8, 6, 0, 0, TimeSpan.Zero));

    private static MetricSample Coverage(string project, DateOnly day, double percent) =>
        new(project, MetricNames.CoverageLinePercent, "dotnet-10", day, percent, $"report:{day:yyyy-MM-dd}");

    private static IEnumerable<MetricSample> Ci(string project, DateOnly day, int total, int passed) =>
    [
        new(project, MetricNames.CiRunsTotal, string.Empty, day, total, $"ci:{day:yyyy-MM-dd}"),
        new(project, MetricNames.CiRunsPassed, string.Empty, day, passed, $"ci:{day:yyyy-MM-dd}"),
    ];

    [Fact]
    public async Task Panel_reports_alert_with_both_reasons_when_both_rules_fire()
    {
        var store = new InMemoryMetricStore();
        await store.UpsertAsync(
        [
            Coverage("bazaar", _asOf.AddDays(-2), 80),
            Coverage("bazaar", _asOf.AddDays(-1), 77),
        ], TestContext.Current.CancellationToken);
        foreach (var day in new[] { _asOf.AddDays(-2), _asOf.AddDays(-1), _asOf })
            await store.UpsertAsync([.. Ci("bazaar", day, 2, 1)], TestContext.Current.CancellationToken);

        var panel = await ProjectHealthQuery.GetAsync(store, Clock(), "bazaar", _thresholds, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(HealthStatus.Alert, panel.Status);
        Assert.Equal(_asOf, panel.AsOf);
        Assert.Equal(2, panel.Reasons.Count);
        Assert.Contains(panel.Reasons, r => r.Kind == HealthReasonKind.CoverageDrop);
        Assert.Contains(panel.Reasons, r => r.Kind == HealthReasonKind.BuildFailureStreak);
    }

    [Fact]
    public async Task Panel_is_healthy_when_data_exists_and_no_rule_fires()
    {
        var store = new InMemoryMetricStore();
        await store.UpsertAsync(Ci("bazaar", _asOf, 2, 2).ToList(), TestContext.Current.CancellationToken);

        var panel = await ProjectHealthQuery.GetAsync(store, Clock(), "bazaar", _thresholds, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(HealthStatus.Healthy, panel.Status);
        Assert.Empty(panel.Reasons);
    }

    [Fact]
    public async Task Panel_has_no_data_when_the_store_is_empty()
    {
        var panel = await ProjectHealthQuery.GetAsync(new InMemoryMetricStore(), Clock(), "bazaar", _thresholds, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(HealthStatus.NoData, panel.Status);
        Assert.Empty(panel.Reasons);
    }

    [Fact]
    public async Task Panel_reads_only_the_requested_project()
    {
        var store = new InMemoryMetricStore();
        foreach (var day in new[] { _asOf.AddDays(-2), _asOf.AddDays(-1), _asOf })
            await store.UpsertAsync([.. Ci("other", day, 2, 0)], TestContext.Current.CancellationToken);

        var panel = await ProjectHealthQuery.GetAsync(store, Clock(), "bazaar", _thresholds, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(HealthStatus.NoData, panel.Status);
    }

    [Fact]
    public async Task Samples_older_than_the_window_are_not_judged()
    {
        var store = new InMemoryMetricStore();
        await store.UpsertAsync(
        [
            Coverage("bazaar", _asOf.AddDays(-40), 90),
            Coverage("bazaar", _asOf.AddDays(-39), 60),
        ], TestContext.Current.CancellationToken);

        var panel = await ProjectHealthQuery.GetAsync(store, Clock(), "bazaar", _thresholds, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(HealthStatus.NoData, panel.Status);
    }

    [Fact]
    public async Task Panel_rejects_a_window_too_short_to_compare_days()
    {
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            ProjectHealthQuery.GetAsync(new InMemoryMetricStore(), Clock(), "bazaar", _thresholds, windowDays: 1, cancellationToken: TestContext.Current.CancellationToken));
    }
}
