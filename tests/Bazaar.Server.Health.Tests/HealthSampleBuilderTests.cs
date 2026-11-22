using Bazaar.Server.Knowledge.Metrics;

namespace Bazaar.Server.Health.Tests;

public class HealthSampleBuilderTests
{
    private static readonly DateOnly _day = new(2026, 11, 22);
    private static readonly DateTimeOffset _at = new(2026, 11, 22, 10, 0, 0, TimeSpan.Zero);
    private const string Cobertura = """<coverage line-rate="0.8125"></coverage>""";

    [Fact]
    public void No_facts_give_no_samples()
    {
        Assert.Empty(HealthSampleBuilder.Build("p", _day, HealthFacts.Empty));
    }

    [Fact]
    public void Every_kind_of_fact_becomes_its_metric()
    {
        var facts = new HealthFacts
        {
            CiRuns = [new("r1", _at, true), new("r2", _at, false)],
            Coverage = [new("dotnet", Cobertura, "cov:1")],
            Analyzer = [new("dotnet", """{"runs":[{"results":[{"level":"warning"}]}]}""", "build:1")],
            PackageAudit = new("""{"projects":[]}""", "audit:1"),
            Reverts = [new("abc", _at)],
            Reopens = [new("c1", _at)],
            VoteReversals = [new("v1", _at)],
            Board = new(1, 4),
        };
        var metrics = HealthSampleBuilder.Build("p", _day, facts).Select(s => s.Metric).Order(StringComparer.Ordinal).ToArray();

        Assert.Equal(
            new[]
            {
                MetricNames.CiRunsTotal, MetricNames.CiRunsPassed, MetricNames.CoverageLinePercent,
                HealthMetricNames.AnalyzerWarnings, HealthMetricNames.PackageVulnerabilities, HealthMetricNames.Reverts,
                HealthMetricNames.ReopenedTasks, HealthMetricNames.VoteReversals, HealthMetricNames.BlockedShare,
            }.Order(StringComparer.Ordinal),
            metrics);
    }

    [Fact]
    public void Unreadable_reports_and_an_empty_board_are_gaps()
    {
        var facts = new HealthFacts
        {
            Coverage = [new("dotnet", "<not xml", "cov:1")],
            Analyzer = [new("dotnet", "garbage", "build:1")],
            PackageAudit = new("garbage", "audit:1"),
            Board = new(0, 0),
        };

        Assert.Empty(HealthSampleBuilder.Build("p", _day, facts));
    }

    [Fact]
    public void Merge_joins_lists_and_takes_the_first_single_value()
    {
        var a = new HealthFacts { Reverts = [new("a", _at)], Board = new(1, 2) };
        var b = new HealthFacts { Reverts = [new("b", _at)], Board = new(9, 9), PackageAudit = new("{}", "x") };
        var merged = HealthFacts.Merge([a, b]);

        Assert.Equal(2, merged.Reverts.Count);
        Assert.Equal(new BoardCountsFact(1, 2), merged.Board);
        Assert.Equal("x", merged.PackageAudit!.SourceRef);
    }
}
