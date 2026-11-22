using System.Globalization;
using System.Text.Json;
using Bazaar.Client.Abstractions.Panes;
using Bazaar.Contracts.Panes;

namespace Bazaar.Client.Analytics;

/// <summary>One agent's row. Rates are display text; "no data" when the server sent null (a gap, not a zero).</summary>
public sealed record AnalyticsAgentRow(string AgentId, string TasksPerWeek, string ReviewPassRate, string RevertRate, IReadOnlyList<string> Hints);

/// <summary>
/// Company analytics pane (doc 25). Rows arrive in the server's order (by agent id) and are never re-sorted by any figure: the pane
/// is not a leaderboard. Model: see the server's <c>AnalyticsPaneBuilder</c>.
/// </summary>
public sealed class AnalyticsPaneViewModel : IPaneViewModel
{
    public const string NoData = "no data";

    public AnalyticsPaneViewModel(PaneDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);

        Kind = descriptor.Kind;
        Title = descriptor.Title;
        var company = descriptor.Model.TryGetProperty("company", out var c) && c.ValueKind == JsonValueKind.Object
            ? c
            : throw new PaneModelException("analytics model needs a company object");
        CompanySummary = $"Tasks per week: {Number(company, "tasksPerWeek")} · Review pass rate: {Percent(company, "reviewPassRate")} · Revert rate: {Percent(company, "revertRate")}";
        Agents = PaneValues.Array(descriptor.Model, "agents").EnumerateArray().Select(ReadAgent).ToArray();
    }

    public string Kind { get; }

    public string Title { get; }

    public string CompanySummary { get; }

    public IReadOnlyList<AnalyticsAgentRow> Agents { get; }

    private static AnalyticsAgentRow ReadAgent(JsonElement a)
    {
        if (a.ValueKind != JsonValueKind.Object)
        {
            throw new PaneModelException("each analytics agent must be an object");
        }

        var hints = PaneValues.Array(a, "hints").EnumerateArray()
            .Select(h => h.ValueKind == JsonValueKind.String ? h.GetString()! : throw new PaneModelException("hints must be strings"))
            .ToArray();
        return new AnalyticsAgentRow(
            PaneValues.RequiredString(a, "agentId"),
            Number(a, "tasksPerWeek"),
            Percent(a, "reviewPassRate"),
            Percent(a, "revertRate"),
            hints);
    }

    private static string Number(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number
            ? v.GetDouble().ToString("0.##", CultureInfo.InvariantCulture)
            : NoData;

    private static string Percent(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number
            ? (v.GetDouble() * 100).ToString("0", CultureInfo.InvariantCulture) + "%"
            : NoData;
}
