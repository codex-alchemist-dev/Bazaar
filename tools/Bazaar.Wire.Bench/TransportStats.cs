using System.Globalization;
using System.Text;

namespace Bazaar.Wire.Bench;

/// <summary>One measured (or failed) value of a transport row: <c>transport x condition x metric x run</c>.</summary>
/// <param name="Transport">Transport id such as <c>ws-h2</c>.</param>
/// <param name="Condition">Network condition label, such as <c>clean</c> or <c>loss1-rtt50</c>.</param>
/// <param name="Metric">Metric name.</param>
/// <param name="Run">Process run number, 1-based.</param>
/// <param name="Value">The value, or NaN when the metric failed.</param>
/// <param name="Note">Why it failed, or null.</param>
internal sealed record TransportRow(string Transport, string Condition, string Metric, int Run, double Value, string? Note = null);

/// <summary>Statistics and table formatting for the transport rows. Pure, so it is unit tested.</summary>
internal static class TransportStats
{
    /// <summary>Metric name to unit, in the order the tables list them.</summary>
    public static IReadOnlyList<(string Metric, string Unit)> Metrics { get; } =
    [
        ("connect_cold_ms", "ms"),
        ("connect_warm_ms", "ms"),
        ("replay_first_page_ms", "ms"),
        ("replay_page_ms", "ms"),
        ("throughput_msgs_per_s", "msg/s"),
        ("delivered_fraction", "ratio"),
        ("latency_p50_ms", "ms"),
        ("latency_p99_ms", "ms"),
        ("cpu_ms_per_1000_msgs", "ms"),
        ("reconnect_detect_ms", "ms"),
        ("reconnect_total_ms", "ms"),
    ];

    /// <summary>Median of the finite values; NaN when there are none. Even counts average the two middle values.</summary>
    public static double Median(IEnumerable<double> values)
    {
        var s = values.Where(double.IsFinite).Order().ToArray();
        if (s.Length == 0) return double.NaN;
        return s.Length % 2 == 1 ? s[s.Length / 2] : (s[s.Length / 2 - 1] + s[s.Length / 2]) / 2;
    }

    /// <summary>Nearest-rank percentile (0 to 100) of the values; NaN when there are none.</summary>
    public static double Percentile(IReadOnlyCollection<double> values, double percentile)
    {
        if (values.Count == 0) return double.NaN;
        var s = values.Order().ToArray();
        var rank = (int)Math.Ceiling(percentile / 100.0 * s.Length);
        return s[Math.Clamp(rank, 1, s.Length) - 1];
    }

    /// <summary>Formats a value for a table cell; NaN is <c>n/a</c>.</summary>
    public static string Format(double v) =>
        double.IsNaN(v) ? "n/a" : v.ToString(Math.Abs(v) >= 100 ? "N0" : Math.Abs(v) >= 10 ? "N1" : "N2", CultureInfo.InvariantCulture);

    /// <summary>The TSV: <c>transport, condition, metric, run, value, note</c>.</summary>
    public static string Tsv(IEnumerable<TransportRow> rows)
    {
        var sb = new StringBuilder("transport\tcondition\tmetric\trun\tvalue\tnote\n");
        foreach (var r in rows)
        {
            sb.Append(r.Transport).Append('\t').Append(r.Condition).Append('\t').Append(r.Metric).Append('\t')
              .Append(r.Run.ToString(CultureInfo.InvariantCulture)).Append('\t')
              .Append(double.IsNaN(r.Value) ? string.Empty : r.Value.ToString("R", CultureInfo.InvariantCulture)).Append('\t')
              .Append(r.Note?.Replace('\t', ' ').Replace('\n', ' ') ?? string.Empty).Append('\n');
        }

        return sb.ToString();
    }

    /// <summary>
    /// The markdown tables: one row per transport x metric with the value of each run and the median, then the failures
    /// (a metric with no finite value) with their reason. <paramref name="transports"/> fixes the row order.
    /// </summary>
    public static string Markdown(IReadOnlyList<TransportRow> rows, IReadOnlyList<string> transports, string condition, int runs)
    {
        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $"#### Transport rows, condition `{condition}` (JSON codec, {runs} process runs, median of the runs)\n\n");
        sb.Append("| Transport | Metric | Unit | ");
        for (var i = 1; i <= runs; i++) sb.Append(CultureInfo.InvariantCulture, $"Run {i} | ");
        sb.Append("Median |\n|---|---|---|");
        for (var i = 0; i < runs; i++) sb.Append("---|");
        sb.Append("---|\n");
        foreach (var t in transports)
        {
            foreach (var (metric, unit) in Metrics)
            {
                var mine = rows.Where(r => r.Transport == t && r.Condition == condition && r.Metric == metric && !double.IsNaN(r.Value)).ToList();
                if (mine.Count == 0) continue;
                sb.Append(CultureInfo.InvariantCulture, $"| {t} | {metric} | {unit} | ");
                for (var run = 1; run <= runs; run++)
                {
                    var cell = mine.FirstOrDefault(r => r.Run == run);
                    sb.Append(cell is null ? "missing" : Format(cell.Value)).Append(" | ");
                }

                sb.Append(Format(Median(mine.Select(r => r.Value)))).Append(" |\n");
            }
        }

        var failures = rows.Where(r => r.Condition == condition && double.IsNaN(r.Value)).GroupBy(r => (r.Transport, r.Note)).ToList();
        if (failures.Count > 0)
        {
            sb.Append("\nFailed or not measured in this condition:\n\n| Transport | Metrics | Runs affected | Reason |\n|---|---|---|---|\n");
            foreach (var g in failures)
            {
                var metrics = g.Select(r => r.Metric).Distinct().ToList();
                var all = metrics.Count >= Metrics.Count ? "all" : string.Join(", ", metrics);
                sb.Append(CultureInfo.InvariantCulture, $"| {g.Key.Transport} | {all} | {string.Join(", ", g.Select(r => r.Run).Distinct().Order())} | {g.Key.Note ?? "no value"} |\n");
            }
        }

        sb.Append('\n');
        return sb.ToString();
    }
}
