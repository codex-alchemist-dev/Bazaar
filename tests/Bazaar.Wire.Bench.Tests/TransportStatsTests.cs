using Bazaar.Wire.Bench;

namespace Bazaar.Wire.Bench.Tests;

public sealed class TransportStatsTests
{
    [Fact]
    public void Median_of_an_odd_count_is_the_middle_value()
    {
        Assert.Equal(5, TransportStats.Median([9, 1, 5]));
    }

    [Fact]
    public void Median_of_an_even_count_averages_the_middle_two()
    {
        Assert.Equal(2.5, TransportStats.Median([4, 1, 3, 2]));
    }

    [Fact]
    public void Median_ignores_failed_values_and_is_nan_when_none_are_left()
    {
        Assert.Equal(7, TransportStats.Median([double.NaN, 7]));
        Assert.True(double.IsNaN(TransportStats.Median([double.NaN])));
        Assert.True(double.IsNaN(TransportStats.Median([])));
    }

    [Fact]
    public void Percentile_uses_the_nearest_rank()
    {
        double[] hundred = [.. Enumerable.Range(1, 100).Select(i => (double)i)];
        Assert.Equal(50, TransportStats.Percentile(hundred, 50));
        Assert.Equal(99, TransportStats.Percentile(hundred, 99));
        Assert.Equal(100, TransportStats.Percentile(hundred, 100));
        Assert.Equal(1, TransportStats.Percentile(hundred, 0));
        Assert.True(double.IsNaN(TransportStats.Percentile([], 50)));
    }

    [Fact]
    public void Markdown_has_one_row_per_metric_with_the_runs_and_the_median()
    {
        List<TransportRow> rows =
        [
            new("quic", "clean", "connect_cold_ms", 1, 10),
            new("quic", "clean", "connect_cold_ms", 2, 30),
            new("quic", "clean", "connect_cold_ms", 3, 20),
        ];
        var md = TransportStats.Markdown(rows, ["quic"], "clean", 3);
        Assert.Contains("| quic | connect_cold_ms | ms | 10.0 | 30.0 | 20.0 | 20.0 |", md, StringComparison.Ordinal);
        Assert.DoesNotContain("Failed or not measured", md, StringComparison.Ordinal);
    }

    [Fact]
    public void Markdown_lists_failures_with_their_reason_and_never_a_made_up_number()
    {
        List<TransportRow> rows = [.. TransportStats.Metrics.Select(m => new TransportRow("ws-h3", "clean", m.Metric, 1, double.NaN, "MsQuic missing"))];
        var md = TransportStats.Markdown(rows, ["ws-h3"], "clean", 1);
        Assert.Contains("| ws-h3 | all | 1 | MsQuic missing |", md, StringComparison.Ordinal);
        Assert.DoesNotContain("| ws-h3 | connect_cold_ms |", md, StringComparison.Ordinal);
    }

    [Fact]
    public void Tsv_writes_an_empty_value_for_a_failure()
    {
        var tsv = TransportStats.Tsv([new TransportRow("quic", "clean", "connect_cold_ms", 1, double.NaN, "no\tMsQuic")]);
        Assert.Contains("quic\tclean\tconnect_cold_ms\t1\t\tno MsQuic\n", tsv, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_reads_child_output_and_drops_a_failure_that_has_a_value()
    {
        var rows = new List<TransportRow>();
        var info = new Dictionary<(string Transport, string Key), string>();
        const string output = "INFO\tquic\t1\tclean\tprotocol\tquic\n" +
            "RESULT\tquic\t1\tclean\tconnect_cold_ms\t12.5\n" +
            "FAIL\tquic\t1\tclean\tconnect_cold_ms\tlater step failed\n" +
            "FAIL\tquic\t1\tclean\treplay_page_ms\ttimed out\n";
        TransportOrchestrator.Parse(output, rows, info);
        Assert.Equal(2, rows.Count);
        Assert.Equal(12.5, rows.Single(r => r.Metric == "connect_cold_ms").Value);
        Assert.True(double.IsNaN(rows.Single(r => r.Metric == "replay_page_ms").Value));
        Assert.Equal("quic", info[("quic", "protocol")]);
    }

    [Fact]
    public void Frames_are_the_s6_corpus_encoded_as_json_gc_messages()
    {
        var frames = TransportRunner.BuildFrames();
        Assert.Equal(Corpus.MessageCount, frames.Length);
        Assert.StartsWith("{", System.Text.Encoding.UTF8.GetString(frames[0]), StringComparison.Ordinal);
    }
}
