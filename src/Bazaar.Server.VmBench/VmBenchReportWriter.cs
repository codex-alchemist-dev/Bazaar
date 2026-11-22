using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Bazaar.Server.VmBench.Abstractions;

namespace Bazaar.Server.VmBench;

/// <summary>
/// Renders a benchmark report as Markdown for people and as JSON for tools and run-to-run comparison.
/// Output is deterministic: stats are ordered by scenario, then guest OS, and the same report always renders the same text.
/// </summary>
public sealed class VmBenchReportWriter : IVmBenchReportWriter
{
    /// <summary>The closing line every Markdown report ends with (doc 16 sizing stays open until the owner reviews it).</summary>
    public const string ClosingLine =
        "Benchmark numbers are measurements on this host only; doc 16 sizing stays OPEN until reviewed by the owner.";

    private static readonly BenchGuestOs[] _osOrder = [BenchGuestOs.Linux, BenchGuestOs.Windows];

    public string ToMarkdown(VmBenchReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        var sb = new StringBuilder();
        sb.Append("# VM benchmark ").Append(report.RunId).Append(" on ").Append(report.HostLabel).Append("\n\n");
        sb.Append("- Started: ").Append(FormatTime(report.StartedAt)).Append('\n');
        sb.Append("- Finished: ").Append(FormatTime(report.FinishedAt)).Append('\n');
        var duration = report.FinishedAt - report.StartedAt;
        sb.Append("- Duration: ").Append(Format(duration.TotalSeconds)).Append(" s\n");

        foreach (var os in _osOrder)
        {
            sb.Append('\n').Append("## ").Append(os).Append(" guests\n\n");
            var rows = OrderedStats(report).Where(s => s.Os == os).ToList();
            if (rows.Count == 0)
            {
                sb.Append("_No measurements._\n");
                continue;
            }

            sb.Append("| Scenario | n | failures | min | median | p95 | max | mean | unit |\n");
            sb.Append("| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | --- |\n");
            foreach (var s in rows)
            {
                sb.Append("| ").Append(s.Scenario).Append(" | ")
                  .Append(s.Count.ToString(CultureInfo.InvariantCulture)).Append(" | ")
                  .Append(s.Failures.ToString(CultureInfo.InvariantCulture)).Append(" | ")
                  .Append(Format(s.Min)).Append(" | ")
                  .Append(Format(s.Median)).Append(" | ")
                  .Append(Format(s.P95)).Append(" | ")
                  .Append(Format(s.Max)).Append(" | ")
                  .Append(Format(s.Mean)).Append(" | ")
                  .Append(s.Unit).Append(" |\n");
            }
        }

        var failed = report.Samples
            .Where(s => s.Failed)
            .OrderBy(s => s.Scenario)
            .ThenBy(s => s.Os)
            .ThenBy(s => s.Iteration)
            .ToList();
        if (failed.Count > 0)
        {
            sb.Append("\n## Failed steps\n\n");
            foreach (var f in failed)
            {
                sb.Append("- ").Append(f.Scenario).Append(' ').Append(f.Os)
                  .Append(", iteration ").Append(f.Iteration.ToString(CultureInfo.InvariantCulture))
                  .Append(": ").Append(string.IsNullOrWhiteSpace(f.Note) ? "no note" : f.Note).Append('\n');
            }
        }

        sb.Append('\n').Append(ClosingLine).Append('\n');
        return sb.ToString();
    }

    public string ToJson(VmBenchReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var ordered = report with { Stats = OrderedStats(report).ToList() };
        return JsonSerializer.Serialize(ordered, VmBenchJsonContext.Default.VmBenchReport);
    }

    /// <summary>Reads a report written by <see cref="ToJson"/>, for comparing one run with another.</summary>
    public static VmBenchReport FromJson(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        return JsonSerializer.Deserialize(json, VmBenchJsonContext.Default.VmBenchReport)
            ?? throw new JsonException("The benchmark report JSON is empty.");
    }

    private static IEnumerable<VmBenchStat> OrderedStats(VmBenchReport report) =>
        report.Stats.OrderBy(s => s.Scenario).ThenBy(s => s.Os);

    private static string Format(double value) => value.ToString("F2", CultureInfo.InvariantCulture);

    private static string FormatTime(DateTimeOffset value) =>
        value.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture);
}

/// <summary>Source-generated JSON for the benchmark report: camelCase, indented, enums written as strings.</summary>
[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    Converters = new[] { typeof(JsonStringEnumConverter<VmBenchScenario>), typeof(JsonStringEnumConverter<BenchGuestOs>) })]
[JsonSerializable(typeof(VmBenchReport))]
internal sealed partial class VmBenchJsonContext : JsonSerializerContext;
