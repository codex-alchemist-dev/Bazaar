using Bazaar.Constellation.Vulkan.BenchGate;

namespace Bazaar.Constellation.Vulkan.BenchGate.Tests;

public class BenchGateTests
{
    private const string _header = "section,total_nodes,resident_nodes,instances_drawn,upload_ms,resident_bytes,fits_budget,wall_p50_ms,wall_p95_ms,wall_max_ms,gpu_p50_ms,gpu_p95_ms\n";

    private static string Csv(double p50, double p95, long instances = 2_000_000) =>
        _header + $"nodes,10000000,10000000,{instances},5,160000000,true,1,2,3,{p50.ToString(System.Globalization.CultureInfo.InvariantCulture)},{p95.ToString(System.Globalization.CultureInfo.InvariantCulture)}\n";

    [Fact]
    public void ParseCsv_reads_timed_rows()
    {
        IReadOnlyList<BenchRow> rows = BenchGate.ParseCsv(Csv(1.0, 1.4));
        BenchRow row = Assert.Single(rows);
        Assert.Equal(10_000_000, row.TotalNodes);
        Assert.Equal(1.4, row.GpuP95Ms);
    }

    [Fact]
    public void ParseCsv_rejects_a_header_without_gpu_columns() =>
        Assert.Throws<FormatException>(() => BenchGate.ParseCsv("section,total_nodes\nx,1\n"));

    [Fact]
    public void Same_numbers_pass()
    {
        GateReport r = BenchGate.Compare(BenchGate.ParseCsv(Csv(1.0, 1.4)), BenchGate.ParseCsv(Csv(1.0, 1.4)), new GateOptions());
        Assert.True(r.Passed);
    }

    [Fact]
    public void A_large_slowdown_fails()
    {
        GateReport r = BenchGate.Compare(BenchGate.ParseCsv(Csv(1.0, 1.4)), BenchGate.ParseCsv(Csv(2.0, 1.4)), new GateOptions());
        Assert.False(r.Passed);
        Assert.Contains(r.Rows, x => x.Verdict == GateVerdict.Regressed && x.Metric == "gpu_p50_ms");
    }

    [Fact]
    public void A_sub_floor_difference_is_noise_even_at_a_high_ratio()
    {
        // 0.04 ms to 0.09 ms is 2.25x but only 0.05 ms, under the 0.25 ms floor.
        GateReport r = BenchGate.Compare(BenchGate.ParseCsv(Csv(0.04, 0.05)), BenchGate.ParseCsv(Csv(0.09, 0.1)), new GateOptions());
        Assert.True(r.Passed);
    }

    [Fact]
    public void A_baseline_row_the_candidate_dropped_fails()
    {
        GateReport r = BenchGate.Compare(BenchGate.ParseCsv(Csv(1.0, 1.4)), BenchGate.ParseCsv(Csv(1.0, 1.4, instances: 1_000_000)), new GateOptions());
        Assert.False(r.Passed);
        Assert.Contains(r.Rows, x => x.Verdict == GateVerdict.Missing);
        Assert.Contains(r.Rows, x => x.Verdict == GateVerdict.New);
    }

    [Fact]
    public void Faster_candidates_pass()
    {
        GateReport r = BenchGate.Compare(BenchGate.ParseCsv(Csv(5.0, 6.0)), BenchGate.ParseCsv(Csv(1.0, 1.4)), new GateOptions());
        Assert.True(r.Passed);
    }
}
