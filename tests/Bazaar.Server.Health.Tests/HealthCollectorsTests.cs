using Bazaar.Server.Knowledge.Metrics;

namespace Bazaar.Server.Health.Tests;

public class HealthCollectorsTests
{
    private static readonly DateOnly _day = new(2026, 11, 22);

    [Fact]
    public void Sarif_counts_only_warning_level_results_across_runs()
    {
        const string sarif = """
            { "runs": [
              { "results": [ { "level": "warning" }, { "level": "error" }, { "level": "warning" } ] },
              { "results": [ { "level": "note" }, { "level": "warning" } ] },
              { "tool": {} } ] }
            """;
        var s = HealthCollectors.AnalyzerWarnings("p", "dotnet", _day, sarif, "build:1")!;

        Assert.Equal(3, s.Value);
        Assert.Equal(HealthMetricNames.AnalyzerWarnings, s.Metric);
        Assert.Equal("dotnet", s.Dimension);
    }

    [Fact]
    public void A_clean_sarif_log_is_zero_but_garbage_is_a_gap()
    {
        Assert.Equal(0, HealthCollectors.AnalyzerWarnings("p", "dotnet", _day, """{ "runs": [ { "results": [] } ] }""", "b")!.Value);
        Assert.Null(HealthCollectors.AnalyzerWarnings("p", "dotnet", _day, "not json", "b"));
        Assert.Null(HealthCollectors.AnalyzerWarnings("p", "dotnet", _day, """{ "version": "2.1.0" }""", "b"));
        Assert.Null(HealthCollectors.AnalyzerWarnings("p", "dotnet", _day, "[1]", "b"));
    }

    [Fact]
    public void Package_audit_counts_distinct_vulnerable_packages_top_level_and_transitive()
    {
        const string audit = """
            { "projects": [
              { "frameworks": [ {
                "topLevelPackages": [
                  { "id": "A", "resolvedVersion": "1.0", "vulnerabilities": [ { "severity": "High" } ] },
                  { "id": "B", "resolvedVersion": "2.0" } ],
                "transitivePackages": [
                  { "id": "C", "resolvedVersion": "3.0", "vulnerabilities": [ {} ] } ] } ] },
              { "frameworks": [ {
                "topLevelPackages": [ { "id": "A", "resolvedVersion": "1.0", "vulnerabilities": [ {} ] } ] } ] } ] }
            """;
        Assert.Equal(2, HealthCollectors.PackageVulnerabilities("p", _day, audit, "audit:1")!.Value);
        Assert.Equal(0, HealthCollectors.PackageVulnerabilities("p", _day, """{ "projects": [] }""", "a")!.Value);
        Assert.Null(HealthCollectors.PackageVulnerabilities("p", _day, "oops", "a"));
    }

    [Fact]
    public void Reverts_count_per_day_dedupe_shas_and_skip_empty_days()
    {
        var at = new DateTimeOffset(2026, 11, 22, 9, 0, 0, TimeSpan.Zero);
        var samples = HealthCollectors.Reverts("p",
        [
            new("a", at), new("a", at.AddHours(1)), new("b", at.AddHours(2)),
            new("c", at.AddDays(-2)),
        ]);

        Assert.Equal([1d, 2d], samples.Select(s => s.Value));
        Assert.Equal([_day.AddDays(-2), _day], samples.Select(s => s.Bucket));
        Assert.Empty(HealthCollectors.Reverts("p", []));
    }

    [Fact]
    public void Reopened_tasks_and_vote_reversals_use_their_own_metrics()
    {
        var at = new DateTimeOffset(2026, 11, 22, 1, 0, 0, TimeSpan.Zero);
        var reopened = HealthCollectors.ReopenedTasks("p", [new("c1", at), new("c2", at)]);
        var reversed = HealthCollectors.VoteReversals("p", [new("v1", at)]);

        Assert.Equal(HealthMetricNames.ReopenedTasks, Assert.Single(reopened).Metric);
        Assert.Equal(2, reopened[0].Value);
        Assert.Equal(HealthMetricNames.VoteReversals, Assert.Single(reversed).Metric);
    }

    [Fact]
    public void Blocked_share_is_a_ratio_and_an_empty_board_is_a_gap()
    {
        Assert.Equal(0.25, HealthCollectors.BlockedShare("p", _day, 1, 4)!.Value);
        Assert.Equal(0, HealthCollectors.BlockedShare("p", _day, 0, 4)!.Value);
        Assert.Null(HealthCollectors.BlockedShare("p", _day, 0, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => HealthCollectors.BlockedShare("p", _day, 5, 4));
    }

    [Fact]
    public async Task Collected_samples_flow_through_the_store_into_the_report()
    {
        var store = new InMemoryMetricStore();
        var at = new DateTimeOffset(2026, 11, 22, 8, 0, 0, TimeSpan.Zero);
        await store.UpsertAsync([.. HealthCollectors.Reverts("p", [new("a", at)]), HealthCollectors.BlockedShare("p", _day, 1, 5)!], TestContext.Current.CancellationToken);
        var clock = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(at);
        var report = await new ProjectHealthService(store, clock).GetAsync("p", TestContext.Current.CancellationToken);

        Assert.Equal(1, report.TechDebt.RevertsInWindow);
        Assert.Equal(0.2, report.Morale.Single(m => m.Signal == MoraleSignals.BlockedShare).Trend!.Latest);
    }
}
