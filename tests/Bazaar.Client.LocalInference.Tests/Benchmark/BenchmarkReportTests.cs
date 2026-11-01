using Bazaar.Client.LocalInference.Benchmark;

namespace Bazaar.Client.LocalInference.Tests.Benchmark;

public class BenchmarkReportTests
{
    private static readonly DateTimeOffset _stamp = new(2026, 10, 30, 9, 30, 0, TimeSpan.FromHours(1));

    private static BenchmarkReport SampleReport() =>
        BenchmarkReport.From(
        [
            new BenchmarkRecord("c01", BackendTier.Npu, 12.5, false, null, true),
            new BenchmarkRecord("c02", BackendTier.Npu, 30, true, "TimeoutException: slow", false),
            new BenchmarkRecord("c01", BackendTier.Cpu, 80, false, null, true),
        ], _stamp);

    [Fact]
    public void Report_round_trips_through_json()
    {
        var report = SampleReport();

        var restored = BenchmarkReport.FromJson(report.ToJson());

        Assert.Equal(report.GeneratedAt, restored.GeneratedAt);
        Assert.Equal(report.Runs, restored.Runs);
        Assert.Equal(report.Errors, restored.Errors);
        Assert.Equal(report.ErrorRate, restored.ErrorRate);
        Assert.Equal(report.GuardPassed, restored.GuardPassed);
        Assert.Equal(report.P50Ms, restored.P50Ms);
        Assert.Equal(report.P95Ms, restored.P95Ms);
        Assert.Equal(report.Records, restored.Records);
        Assert.Equal(report.Tiers, restored.Tiers);
        Assert.Equal(report.ToJson(), restored.ToJson());
    }

    [Fact]
    public void Json_uses_camel_case_names_and_tier_names()
    {
        var json = SampleReport().ToJson();

        Assert.Contains("\"errorRate\":", json, StringComparison.Ordinal);
        Assert.Contains("\"tier\": \"Npu\"", json, StringComparison.Ordinal);
        Assert.Contains("\"itemId\": \"c02\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Null_percentiles_survive_the_round_trip_for_an_empty_report()
    {
        var report = BenchmarkReport.From([], _stamp);

        var restored = BenchmarkReport.FromJson(report.ToJson());

        Assert.Equal(0, restored.Runs);
        Assert.Null(restored.P50Ms);
        Assert.Null(restored.P95Ms);
        Assert.Empty(restored.Tiers);
    }

    [Fact]
    public void From_computes_totals_and_tier_order_by_first_appearance()
    {
        var report = SampleReport();
        BackendTier[] expectedTiers = [BackendTier.Npu, BackendTier.Cpu];

        Assert.Equal(3, report.Runs);
        Assert.Equal(1, report.Errors);
        Assert.Equal(2, report.GuardPassed);
        Assert.Equal(expectedTiers, report.Tiers.Select(tier => tier.Tier));
        Assert.Equal(30.0, report.P50Ms);
        Assert.Equal(80.0, report.P95Ms);
    }
}
