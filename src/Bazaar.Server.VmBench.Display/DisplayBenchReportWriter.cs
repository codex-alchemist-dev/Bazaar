using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Bazaar.Server.VmBench.Display;

/// <summary>
/// Renders a display benchmark report. Criteria come first so the thresholds sit next to the results they judged. The report holds
/// sizes and timings only; no frame bytes or hashes ever reach it (doc 59 section 5).
/// </summary>
public sealed class DisplayBenchReportWriter : IDisplayBenchReportWriter
{
    /// <summary>The closing line every Markdown report ends with.</summary>
    public const string ClosingLine =
        "Criteria are provisional until the first measured run on the owner's host (spike S-DSP); a pass here is a pass against those numbers only.";

    public string ToMarkdown(DisplayBenchReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        var sb = new StringBuilder();
        sb.Append("# Display benchmark ").Append(report.RunId).Append(" on ").Append(report.HostLabel).Append("\n\n");
        sb.Append("- Started: ").Append(Time(report.StartedAt)).Append('\n');
        sb.Append("- Finished: ").Append(Time(report.FinishedAt)).Append('\n');
        sb.Append("- Overall: ").Append(report.Overall).Append("\n\n");

        sb.Append("## Criteria (fixed at run start)\n\n| Setting | Value |\n| --- | ---: |\n");
        foreach (var p in typeof(DisplayBenchCriteriaSnapshot).GetProperties().OrderBy(p => p.Name, StringComparer.Ordinal))
        {
            sb.Append("| ").Append(p.Name).Append(" | ")
              .Append(Convert.ToString(p.GetValue(report.Criteria), CultureInfo.InvariantCulture)).Append(" |\n");
        }

        sb.Append("\n## Results\n\n");
        if (report.Results.Count == 0)
        {
            sb.Append("_No scenarios ran._\n");
        }
        else
        {
            sb.Append("| Scenario | Variant | Outcome | Value | Limit | Unit | n | p95 | Note |\n| --- | --- | --- | ---: | ---: | --- | ---: | ---: | --- |\n");
            foreach (var r in report.Results)
            {
                sb.Append("| ").Append(r.Scenario).Append(" | ").Append(r.Variant).Append(" | ").Append(r.Outcome).Append(" | ")
                  .Append(Num(r.Value)).Append(" | ").Append(Num(r.Threshold)).Append(" | ").Append(r.Unit).Append(" | ")
                  .Append(r.Stat is null ? "-" : r.Stat.Count.ToString(CultureInfo.InvariantCulture)).Append(" | ")
                  .Append(r.Stat is null ? "-" : Num(r.Stat.P95)).Append(" | ")
                  .Append(r.Reason is null ? "" : r.Reason.Replace("|", "/", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal)).Append(" |\n");
            }

            var withMetrics = report.Results.Where(r => r.Metrics.Count > 0).ToList();
            if (withMetrics.Count > 0)
            {
                sb.Append("\n## Metrics\n\n");
                foreach (var r in withMetrics)
                {
                    sb.Append("- ").Append(r.Scenario).Append(' ').Append(r.Variant).Append(": ")
                      .Append(string.Join(", ", r.Metrics.Select(m => m.Name + " " + Num(m.Value) + " " + m.Unit))).Append('\n');
                }
            }
        }

        sb.Append('\n').Append(ClosingLine).Append('\n');
        return sb.ToString();
    }

    public string ToJson(DisplayBenchReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        return JsonSerializer.Serialize(report, DisplayBenchJsonContext.Default.DisplayBenchReport);
    }

    private static string Num(double? value) => value is null ? "-" : value.Value.ToString("F2", CultureInfo.InvariantCulture);

    private static string Time(DateTimeOffset value) => value.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture);
}

/// <summary>Source-generated JSON for the display report: camelCase, indented, enums as strings.</summary>
[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    Converters = new[] { typeof(JsonStringEnumConverter<DisplayBenchScenario>), typeof(JsonStringEnumConverter<DisplayBenchOutcome>) })]
[JsonSerializable(typeof(DisplayBenchReport))]
internal sealed partial class DisplayBenchJsonContext : JsonSerializerContext;
