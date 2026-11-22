using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Bazaar.Server.Health;

/// <summary>One labelled figure on the card.</summary>
public sealed record HealthCardItem(string Label, string Value);

/// <summary>A dashboard section for the project health card, in the shape the client dashboard pane reads (<c>title</c> and <c>items</c>).</summary>
public sealed record HealthCardSection(string Title, IReadOnlyList<HealthCardItem> Items);

/// <summary>
/// Turns a <see cref="ProjectHealthReport"/> into the dashboard card (doc 25 "Client shows": tech debt, coverage trend, build pass
/// rate, blocked share). The server decides what the card says; the client only lays it out. A missing value reads "no data",
/// never zero.
/// </summary>
public static class HealthDashboardCard
{
    public const string NoData = "no data";

    public static HealthCardSection Build(ProjectHealthReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        var items = new List<HealthCardItem> { new("Status", report.Status.ToString()) };
        foreach (var alert in report.Alerts)
            items.Add(new HealthCardItem("Alert", alert.Message));

        foreach (var c in report.Coverage)
            items.Add(new HealthCardItem($"Coverage ({c.Toolchain})", Percent(c.Trend.Latest) + Change(c.Trend)));
        if (report.Coverage.Count == 0)
            items.Add(new HealthCardItem("Coverage", NoData));

        items.Add(new HealthCardItem("Build pass rate", report.Build.PassRate is { } r
            ? $"{Number(r * 100)}% ({report.Build.Passed} of {report.Build.Runs})"
            : NoData));

        items.Add(new HealthCardItem("Analyzer warnings", Count(report.TechDebt.AnalyzerWarnings)));
        items.Add(new HealthCardItem("Vulnerable packages", Count(report.TechDebt.PackageVulnerabilities)));
        items.Add(new HealthCardItem("Reverts", report.TechDebt.RevertsInWindow is { } v ? Number(v) : NoData));

        foreach (var m in report.Morale)
            items.Add(new HealthCardItem(Label(m.Signal), m.Trend is { } t ? Number(t.Latest) + Change(t) : NoData));

        return new HealthCardSection($"Project health: {report.ProjectId}", items);
    }

    /// <summary>The pane model JSON the dashboard pane view model reads: <c>{ "sections": [ ... ] }</c>.</summary>
    public static string ToPaneModel(params HealthCardSection[] sections)
    {
        var root = new JsonObject
        {
            ["sections"] = new JsonArray(sections.Select(s => (JsonNode)new JsonObject
            {
                ["title"] = s.Title,
                ["items"] = new JsonArray(s.Items.Select(i => (JsonNode)new JsonObject { ["label"] = i.Label, ["value"] = i.Value }).ToArray()),
            }).ToArray()),
        };
        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
    }

    private static string Count(MetricTrend? t) => t is null ? NoData : Number(t.Latest) + Change(t);

    private static string Change(MetricTrend t) => t.Change switch
    {
        null or 0 => string.Empty,
        > 0 => $" (+{Number(t.Change.Value)})",
        _ => $" ({Number(t.Change.Value)})",
    };

    private static string Percent(double v) => Number(v) + "%";

    private static string Number(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);

    private static string Label(string signal) => signal switch
    {
        MoraleSignals.BlockedShare => "Blocked share",
        MoraleSignals.ReopenedTasks => "Reopened tasks",
        MoraleSignals.VoteReversals => "Vote reversals",
        _ => signal,
    };
}
