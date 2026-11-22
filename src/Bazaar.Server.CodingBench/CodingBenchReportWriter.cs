using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Bazaar.Server.CodingBench.Abstractions;

namespace Bazaar.Server.CodingBench;

/// <summary>
/// Renders a finished benchmark as Markdown for people and JSON for tools (docs/design/17-coding-agent-harness.md, "Testing").
/// </summary>
public sealed class CodingBenchReportWriter : ICodingBenchReportWriter
{
    private const string _noCostLabel = "n/a (free lane)";
    private const string _modelOnlyNote = "The benchmark may change a model's edit format, not the model (doc 17, conflict 3).";

    private static readonly CodingTaskKind[] _trapKinds =
    [
        CodingTaskKind.TrapWrongTest,
        CodingTaskKind.TrapImpossible,
        CodingTaskKind.TrapInjectedIssue,
    ];

    /// <inheritdoc />
    public string ToMarkdown(CodingBenchReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        var builder = new StringBuilder();
        builder.AppendLine("# Coding benchmark report");
        builder.AppendLine();
        builder.AppendLine(CultureInfo.InvariantCulture, $"- Run: `{report.RunId}`");
        builder.AppendLine(CultureInfo.InvariantCulture, $"- Started: {FormatTime(report.StartedAt)}");
        builder.AppendLine(CultureInfo.InvariantCulture, $"- Finished: {FormatTime(report.FinishedAt)}");
        builder.AppendLine(CultureInfo.InvariantCulture, $"- Runs: {report.Results.Count}");
        builder.AppendLine();

        AppendSummaryTable(builder, report);
        AppendRecommendations(builder, report);
        AppendTraps(builder, report);

        builder.AppendLine(_modelOnlyNote);
        return builder.ToString();
    }

    /// <inheritdoc />
    public string ToJson(CodingBenchReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        return JsonSerializer.Serialize(report, CodingBenchJsonContext.Default.CodingBenchReport);
    }

    /// <summary>Reads a report written by <see cref="ToJson"/>.</summary>
    public static CodingBenchReport FromJson(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        return JsonSerializer.Deserialize(json, CodingBenchJsonContext.Default.CodingBenchReport)
            ?? throw new InvalidDataException("The coding benchmark report JSON is empty.");
    }

    private static void AppendSummaryTable(StringBuilder builder, CodingBenchReport report)
    {
        builder.AppendLine("## Summary");
        builder.AppendLine();
        builder.AppendLine("| Model | Format | Runs | Pass | Format compliance | Malformed | Fake success | Tokens/task | Turns | Cost |");
        builder.AppendLine("|---|---|---:|---:|---:|---:|---:|---:|---:|---:|");
        foreach (var s in OrderedSummaries(report.Summaries))
        {
            var cost = s.TotalCostUsd.HasValue
                ? "$" + s.TotalCostUsd.Value.ToString("0.0000", CultureInfo.InvariantCulture)
                : _noCostLabel;
            builder.AppendLine(CultureInfo.InvariantCulture, $"| {s.Model} | {s.Format} | {s.Runs} | {Percent(s.PassRate)} | {Percent(s.FormatCompliance)} | {Percent(s.MalformedEditRate)} | {Percent(s.FakeSuccessRate)} | {Number(s.MeanTokensPerTask)} | {Number(s.MeanTurns)} | {cost} |");
        }

        builder.AppendLine();
        builder.AppendLine("Cost sources are recorded per run in the JSON report (`results[].outcome.costSource`). \"n/a (free lane)\" means no cost was recorded for that model and format.");
        builder.AppendLine();
    }

    private static void AppendRecommendations(StringBuilder builder, CodingBenchReport report)
    {
        builder.AppendLine("## Recommended edit format");
        builder.AppendLine();
        if (report.RecommendedFormat.Count == 0)
        {
            builder.AppendLine("No models in the report.");
        }

        foreach (var pair in report.RecommendedFormat.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            builder.AppendLine(CultureInfo.InvariantCulture, $"- {pair.Key}: {pair.Value}");
        }

        builder.AppendLine();
        builder.AppendLine("A format needs at least 3 runs to be recommended; otherwise the model keeps whole-file edits (doc 17 default).");
        builder.AppendLine();
    }

    private static void AppendTraps(StringBuilder builder, CodingBenchReport report)
    {
        builder.AppendLine("## Traps");
        builder.AppendLine();
        builder.AppendLine("| Model | Trap | Correct | Runs |");
        builder.AppendLine("|---|---|---:|---:|");

        var models = report.Results.Select(r => r.Request.Model).Distinct(StringComparer.Ordinal).OrderBy(m => m, StringComparer.Ordinal);
        foreach (var model in models)
        {
            foreach (var kind in _trapKinds)
            {
                var runs = report.Results.Where(r => r.Request.Model == model && r.Request.Task.Kind == kind).ToList();
                if (runs.Count == 0)
                {
                    continue;
                }

                var correct = runs.Count(r => r.Correct);
                builder.AppendLine(CultureInfo.InvariantCulture, $"| {model} | {kind} | {correct} | {runs.Count} |");
            }
        }

        builder.AppendLine();
    }

    private static IEnumerable<CodingBenchSummary> OrderedSummaries(IEnumerable<CodingBenchSummary> summaries) =>
        summaries.OrderBy(s => s.Model, StringComparer.Ordinal).ThenBy(s => s.Format);

    private static string Percent(double value) =>
        (value * 100).ToString("0.0", CultureInfo.InvariantCulture) + "%";

    private static string Number(double value) =>
        value.ToString("0.0", CultureInfo.InvariantCulture);

    private static string FormatTime(DateTimeOffset time) =>
        time.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture);
}

/// <summary>Source-generated JSON for <see cref="CodingBenchReport"/>: camelCase, enums as strings, indented.</summary>
[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(CodingBenchReport))]
public sealed partial class CodingBenchJsonContext : JsonSerializerContext
{
}
