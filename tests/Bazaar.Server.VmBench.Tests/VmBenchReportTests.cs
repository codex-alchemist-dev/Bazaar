using Bazaar.Server.VmBench;
using Bazaar.Server.VmBench.Abstractions;

namespace Bazaar.Server.VmBench.Tests;

public sealed class VmBenchReportTests
{
    private static readonly DateTimeOffset _start = new(2026, 11, 19, 9, 0, 0, TimeSpan.Zero);

    private static VmBenchStat Stat(VmBenchScenario scenario, BenchGuestOs os, string unit, double median, int count = 5, int failures = 0) =>
        new(scenario, os, unit, count, failures, median - 1, median, median + 2, median + 3, median + 0.5);

    private static VmBenchReport Report(params VmBenchStat[] stats) => new(
        RunId: "run-42",
        StartedAt: _start,
        FinishedAt: _start.AddSeconds(90.5),
        HostLabel: "bench-host",
        Options: new VmBenchOptions(Iterations: 5, WarmupIterations: 1, ParallelPairCounts: [1, 2]),
        Samples:
        [
            new VmBenchSample(VmBenchScenario.BootToReady, BenchGuestOs.Linux, 1, 12.5, "s", _start),
            new VmBenchSample(VmBenchScenario.Restore, BenchGuestOs.Windows, 3, 0, "s", _start, Failed: true, Note: "guest agent timed out"),
        ],
        Stats: stats);

    private static VmBenchReport SampleReport() => Report(
        Stat(VmBenchScenario.BootToReady, BenchGuestOs.Windows, "s", 20.0),
        Stat(VmBenchScenario.BootToReady, BenchGuestOs.Linux, "s", 12.0),
        Stat(VmBenchScenario.IdleRam, BenchGuestOs.Linux, "bytes", 1024.0 * 1024 * 512),
        Stat(VmBenchScenario.ShareThroughput, BenchGuestOs.Linux, "bytes/s", 150.25, failures: 1));

    [Fact]
    public void Markdown_has_title_tables_failed_steps_and_closing_line()
    {
        var md = new VmBenchReportWriter().ToMarkdown(SampleReport());

        Assert.Contains("# VM benchmark run-42 on bench-host", md);
        Assert.Contains("## Linux guests", md);
        Assert.Contains("## Windows guests", md);
        Assert.Contains("| Scenario | n | failures | min | median | p95 | max | mean | unit |", md);
        Assert.Contains("| BootToReady | 5 | 0 | 11.00 | 12.00 | 14.00 | 15.00 | 12.50 | s |", md);
        Assert.Contains("| ShareThroughput | 5 | 1 |", md);
        Assert.Contains("- Duration: 90.50 s", md);
        Assert.Contains("## Failed steps", md);
        Assert.Contains("- Restore Windows, iteration 3: guest agent timed out", md);
        Assert.EndsWith(VmBenchReportWriter.ClosingLine + "\n", md);
    }

    [Fact]
    public void Markdown_omits_failed_steps_section_when_none_failed()
    {
        var report = SampleReport() with { Samples = [new VmBenchSample(VmBenchScenario.Save, BenchGuestOs.Linux, 1, 2.0, "s", _start)] };

        var md = new VmBenchReportWriter().ToMarkdown(report);

        Assert.DoesNotContain("## Failed steps", md);
    }

    [Fact]
    public void Markdown_orders_stats_by_scenario_then_os_regardless_of_input_order()
    {
        var shuffled = Report(
            Stat(VmBenchScenario.IdleRam, BenchGuestOs.Linux, "bytes", 10),
            Stat(VmBenchScenario.BootToReady, BenchGuestOs.Windows, "s", 20),
            Stat(VmBenchScenario.BootToReady, BenchGuestOs.Linux, "s", 12));

        var md = new VmBenchReportWriter().ToMarkdown(shuffled);

        // Enum declaration order: IdleRam is declared before BootToReady.
        Assert.True(md.IndexOf("| IdleRam |", StringComparison.Ordinal) < md.IndexOf("| BootToReady |", StringComparison.Ordinal));
    }

    [Fact]
    public void Json_round_trips_key_fields()
    {
        var writer = new VmBenchReportWriter();
        var report = SampleReport();

        var back = VmBenchReportWriter.FromJson(writer.ToJson(report));

        Assert.Equal(report.RunId, back.RunId);
        Assert.Equal(report.HostLabel, back.HostLabel);
        Assert.Equal(report.StartedAt, back.StartedAt);
        Assert.Equal(report.FinishedAt, back.FinishedAt);
        Assert.Equal(report.Options.Iterations, back.Options.Iterations);
        Assert.Equal(report.Options.ParallelPairCounts, back.Options.ParallelPairCounts);
        Assert.Equal(report.Samples.Count, back.Samples.Count);
        Assert.Equal(report.Samples[1].Note, back.Samples[1].Note);
        Assert.True(back.Samples[1].Failed);
        Assert.Equal(report.Stats.Count, back.Stats.Count);
        Assert.Equal(report.Stats.Single(s => s.Scenario == VmBenchScenario.ShareThroughput).Median,
            back.Stats.Single(s => s.Scenario == VmBenchScenario.ShareThroughput).Median);
    }

    [Fact]
    public void Json_uses_camel_case_indented_and_string_enums()
    {
        var json = new VmBenchReportWriter().ToJson(SampleReport());

        Assert.Contains("\"runId\": \"run-42\"", json);
        Assert.Contains("\"scenario\": \"BootToReady\"", json);
        Assert.Contains("\"os\": \"Linux\"", json);
        Assert.Contains("\n  \"", json);
    }

    [Fact]
    public void Output_is_deterministic_for_the_same_report()
    {
        var writer = new VmBenchReportWriter();
        var report = SampleReport();

        Assert.Equal(writer.ToMarkdown(report), writer.ToMarkdown(report));
        Assert.Equal(writer.ToJson(report), writer.ToJson(report));
    }

    [Fact]
    public void Json_stats_are_ordered_by_scenario_then_os()
    {
        var shuffled = Report(
            Stat(VmBenchScenario.IdleRam, BenchGuestOs.Linux, "bytes", 10),
            Stat(VmBenchScenario.BootToReady, BenchGuestOs.Windows, "s", 20));

        var back = VmBenchReportWriter.FromJson(new VmBenchReportWriter().ToJson(shuffled));

        Assert.Equal(VmBenchScenario.IdleRam, back.Stats[0].Scenario);
        Assert.Equal(VmBenchScenario.BootToReady, back.Stats[1].Scenario);
    }

    [Fact]
    public void FromJson_rejects_null_input()
    {
        Assert.Throws<ArgumentNullException>(() => VmBenchReportWriter.FromJson(null!));
    }
}
