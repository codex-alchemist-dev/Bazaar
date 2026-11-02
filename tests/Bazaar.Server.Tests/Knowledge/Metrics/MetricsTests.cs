using Bazaar.Server.Knowledge.Metrics;

namespace Bazaar.Server.Tests.Knowledge.Metrics;

public sealed class MetricsTests
{
    private static readonly DateTimeOffset _monday = new(2026, 10, 26, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Ci_collector_counts_runs_per_utc_day_and_passes()
    {
        var samples = CiRunCollector.Collect("bazaar", [
            new CiRunFact("1", _monday, true),
            new CiRunFact("2", _monday.AddHours(2), false),
            new CiRunFact("3", _monday.AddDays(1), true),
        ]);

        Assert.Contains(samples, s => s.Metric == MetricNames.CiRunsTotal && s.Bucket == new DateOnly(2026, 10, 26) && s.Value == 2);
        Assert.Contains(samples, s => s.Metric == MetricNames.CiRunsPassed && s.Bucket == new DateOnly(2026, 10, 26) && s.Value == 1);
        Assert.Contains(samples, s => s.Metric == MetricNames.CiRunsTotal && s.Bucket == new DateOnly(2026, 10, 27) && s.Value == 1);
    }

    [Fact]
    public void A_day_without_runs_is_a_gap_not_a_zero()
    {
        var samples = CiRunCollector.Collect("bazaar", [new CiRunFact("1", _monday, true)]);

        Assert.DoesNotContain(samples, s => s.Bucket == new DateOnly(2026, 10, 27));
        Assert.All(samples, s => Assert.NotEqual(0, s.Value));
    }

    [Fact]
    public void A_repeated_run_id_in_one_batch_is_counted_once()
    {
        var samples = CiRunCollector.Collect("bazaar", [new CiRunFact("1", _monday, true), new CiRunFact("1", _monday, true)]);

        Assert.Equal(1, samples.Single(s => s.Metric == MetricNames.CiRunsTotal).Value);
    }

    [Fact]
    public void Coverage_parser_reads_line_rate_as_percent_under_its_toolchain()
    {
        const string report = """<?xml version="1.0"?><coverage line-rate="0.8765" version="1.9"></coverage>""";

        var sample = CoverageCollector.Parse("bazaar", "dotnet-10", new DateOnly(2026, 10, 26), report, "report:abc");

        Assert.NotNull(sample);
        Assert.Equal(MetricNames.CoverageLinePercent, sample.Metric);
        Assert.Equal("dotnet-10", sample.Dimension);
        Assert.Equal(87.65, sample.Value);
    }

    [Theory]
    [InlineData("""<coverage/>""")]
    [InlineData("""<coverage line-rate="1.5"/>""")]
    [InlineData("""<coverage line-rate="abc"/>""")]
    public void Coverage_parser_returns_null_for_unusable_reports(string report)
    {
        Assert.Null(CoverageCollector.Parse("bazaar", "dotnet-10", new DateOnly(2026, 10, 26), report, "report:x"));
    }

    [Fact]
    public async Task Upsert_is_idempotent_and_query_filters_by_range()
    {
        var store = new InMemoryMetricStore();
        var batch = CiRunCollector.Collect("bazaar", [new CiRunFact("1", _monday, true)]);

        var first = await store.UpsertAsync(batch, TestContext.Current.CancellationToken);
        var second = await store.UpsertAsync(batch, TestContext.Current.CancellationToken);
        var inRange = await store.QueryAsync("bazaar", MetricNames.CiRunsTotal, new DateOnly(2026, 10, 25), new DateOnly(2026, 10, 26), TestContext.Current.CancellationToken);
        var outOfRange = await store.QueryAsync("bazaar", MetricNames.CiRunsTotal, new DateOnly(2026, 10, 27), new DateOnly(2026, 10, 31), TestContext.Current.CancellationToken);

        Assert.Equal(2, first);
        Assert.Equal(0, second);
        Assert.Single(inRange);
        Assert.Empty(outOfRange);
    }

    [Fact]
    public async Task Upsert_replaces_the_value_when_a_source_recomputes()
    {
        var store = new InMemoryMetricStore();
        await store.UpsertAsync(CiRunCollector.Collect("bazaar", [new CiRunFact("1", _monday, true)]), TestContext.Current.CancellationToken);
        await store.UpsertAsync(CiRunCollector.Collect("bazaar", [new CiRunFact("1", _monday, true), new CiRunFact("2", _monday, false)]), TestContext.Current.CancellationToken);

        var rows = await store.QueryAsync("bazaar", MetricNames.CiRunsTotal, new DateOnly(2026, 10, 26), new DateOnly(2026, 10, 26), TestContext.Current.CancellationToken);

        Assert.Equal(2, Assert.Single(rows).Value);
    }

    [Fact]
    public void Rollup_sums_counts_and_averages_rates()
    {
        var day = new DateOnly(2026, 10, 26);
        var samples = new List<MetricSample>
        {
            new("bazaar", MetricNames.CiRunsTotal, string.Empty, day, 2, "ci:a"),
            new("bazaar", MetricNames.CiRunsTotal, string.Empty, day, 3, "ci:b"),
            new("bazaar", MetricNames.CoverageLinePercent, "dotnet-10", day, 80, "r1"),
            new("bazaar", MetricNames.CoverageLinePercent, "dotnet-10", day, 90, "r2"),
        };

        var rows = MetricRollup.Daily(samples);

        Assert.Equal(5, rows.Single(r => r.Metric == MetricNames.CiRunsTotal).Value);
        var coverage = rows.Single(r => r.Metric == MetricNames.CoverageLinePercent);
        Assert.Equal(85, coverage.Value);
        Assert.Equal(2, coverage.Samples);
    }
}
