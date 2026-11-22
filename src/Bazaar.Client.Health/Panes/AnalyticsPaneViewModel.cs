using System.Globalization;
using System.Text.Json;
using Bazaar.Client.Abstractions.Panes;
using Bazaar.Client.Panes.Abstractions;
using Bazaar.Contracts.Panes;

namespace Bazaar.Client.Health.Panes;

/// <summary>
/// The analytics pane (pane kind <see cref="PaneKinds.Analytics"/>, doc 25). The server sends the rollup trends, the
/// project morale signals and an optional per-agent view as data; this view model only formats them. Model:
/// <c>{ "projectId", "windowDays", "series": [ { "metric", "unit", "points": [ { "day", "value" } ] } ], "moraleSignals": [ { "signal", "value" } ],
/// "agentView": { "visibility", "framing", "agents": [ { "agentId", "displayName", "metrics": [ { "metric", "value" } ] } ] } }</c>.
/// A day with no point, or a null value, is a gap in the line and is never shown as zero. The per-agent view is display
/// only: it is ordered by display name, never by value, and carries no rank or score. A missing required field raises
/// <see cref="PaneModelException"/>, so the registry falls back to the generic view.
/// </summary>
public sealed class AnalyticsPaneViewModel : IPaneViewModel
{
    private static readonly string[] _visibilities = ["owner", "owner+self", "peers"];

    public AnalyticsPaneViewModel(PaneDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        Kind = descriptor.Kind;
        Title = "Analytics";

        var model = descriptor.Model;
        ProjectId = PaneValues.RequiredString(model, "projectId");
        WindowDays = PaneValues.RequiredInt(model, "windowDays");
        Series = PaneValues.Array(model, "series").EnumerateArray().Select(ReadSeries).ToArray();
        MoraleSignals = PaneValues.Array(model, "moraleSignals").EnumerateArray().Select(ReadSignal).ToArray();
        AgentView = ReadAgentView(model);
    }

    public string Kind { get; }

    public string Title { get; }

    public string ProjectId { get; }

    public int WindowDays { get; }

    public IReadOnlyList<SeriesRow> Series { get; }

    public IReadOnlyList<MoraleSignalRow> MoraleSignals { get; }

    /// <summary>The optional per-agent view, or null when the server sent none.</summary>
    public AgentViewRow? AgentView { get; }

    private static SeriesRow ReadSeries(JsonElement series)
    {
        if (series.ValueKind != JsonValueKind.Object)
        {
            throw new PaneModelException("each series must be an object");
        }

        var points = PaneValues.Array(series, "points").EnumerateArray().Select(ReadPoint).OrderBy(p => p.Day).ToArray();
        for (var i = 1; i < points.Length; i++)
        {
            if (points[i].Day == points[i - 1].Day)
            {
                throw new PaneModelException($"series has two points for {points[i].Day.ToString(_dayFormat, CultureInfo.InvariantCulture)}");
            }
        }

        return new SeriesRow(
            PaneValues.RequiredString(series, "metric"),
            PaneValues.RequiredString(series, "unit"),
            points,
            FindGaps(points));
    }

    private static SeriesPoint ReadPoint(JsonElement point)
    {
        if (point.ValueKind != JsonValueKind.Object)
        {
            throw new PaneModelException("each point must be an object");
        }

        if (!point.TryGetProperty("value", out var value) || value.ValueKind is not (JsonValueKind.Number or JsonValueKind.Null))
        {
            throw new PaneModelException("point 'value' must be a number or null");
        }

        return new SeriesPoint(ReadDay(point), value.ValueKind == JsonValueKind.Number ? value.GetDouble() : null);
    }

    /// <summary>Lists the days with no value inside the series' span, as runs. A day with no point is a gap, never zero.</summary>
    internal static IReadOnlyList<DayRange> FindGaps(IReadOnlyList<SeriesPoint> points)
    {
        if (points.Count == 0)
        {
            return [];
        }

        var valued = points.Where(p => p.Value is not null).Select(p => p.Day).ToHashSet();
        var first = points[0].Day;
        var last = points[^1].Day;
        var gaps = new List<DayRange>();
        DateOnly? runStart = null;
        DateOnly runEnd = first;

        for (var day = first; ; day = day.AddDays(1))
        {
            if (valued.Contains(day))
            {
                if (runStart is { } start)
                {
                    gaps.Add(new DayRange(start, runEnd));
                    runStart = null;
                }
            }
            else
            {
                runStart ??= day;
                runEnd = day;
            }

            if (day == last)
            {
                break;
            }
        }

        if (runStart is { } open)
        {
            gaps.Add(new DayRange(open, runEnd));
        }

        return gaps;
    }

    private static MoraleSignalRow ReadSignal(JsonElement signal)
    {
        if (signal.ValueKind != JsonValueKind.Object)
        {
            throw new PaneModelException("each morale signal must be an object");
        }

        return new MoraleSignalRow(PaneValues.RequiredString(signal, "signal"), PaneValues.Scalar(signal, "value"));
    }

    private static AgentViewRow? ReadAgentView(JsonElement model)
    {
        if (!model.TryGetProperty("agentView", out var view) || view.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (view.ValueKind != JsonValueKind.Object)
        {
            throw new PaneModelException("'agentView' must be an object");
        }

        var visibility = PaneValues.RequiredString(view, "visibility");
        if (Array.IndexOf(_visibilities, visibility) < 0)
        {
            throw new PaneModelException($"unknown agent view visibility '{visibility}'");
        }

        var agents = PaneValues.Array(view, "agents").EnumerateArray().Select(ReadAgent).ToArray();

        // Ordered by display name, then id as a tie-break. Never by a metric value, so the list cannot read as a ranking.
        var ordered = agents
            .OrderBy(a => a.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(a => a.AgentId, StringComparer.Ordinal)
            .ToArray();
        return new AgentViewRow(visibility, PaneValues.RequiredString(view, "framing"), ordered);
    }

    private static AgentRow ReadAgent(JsonElement agent)
    {
        if (agent.ValueKind != JsonValueKind.Object)
        {
            throw new PaneModelException("each agent must be an object");
        }

        var metrics = PaneValues.Array(agent, "metrics").EnumerateArray().Select(ReadAgentMetric).ToArray();
        return new AgentRow(PaneValues.RequiredString(agent, "agentId"), PaneValues.RequiredString(agent, "displayName"), metrics);
    }

    private static AgentMetricRow ReadAgentMetric(JsonElement metric)
    {
        if (metric.ValueKind != JsonValueKind.Object)
        {
            throw new PaneModelException("each agent metric must be an object");
        }

        return new AgentMetricRow(PaneValues.RequiredString(metric, "metric"), PaneValues.Scalar(metric, "value"));
    }

    private static DateOnly ReadDay(JsonElement parent)
    {
        var text = PaneValues.RequiredString(parent, "day");
        if (!DateOnly.TryParseExact(text, _dayFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
        {
            throw new PaneModelException($"'day' must be a yyyy-MM-dd date, got '{text}'");
        }

        return day;
    }

    private const string _dayFormat = "yyyy-MM-dd";
}

/// <summary>One metric's trend: its points in day order, and the days inside its span that have no value.</summary>
public sealed record SeriesRow(string Metric, string Unit, IReadOnlyList<SeriesPoint> Points, IReadOnlyList<DayRange> Gaps);

/// <summary>One day's value. <see cref="Value"/> is null when the day has a point with no value.</summary>
public sealed record SeriesPoint(DateOnly Day, double? Value);

/// <summary>An inclusive run of days with no value.</summary>
public sealed record DayRange(DateOnly From, DateOnly To);

/// <summary>One project morale signal, shown as its name and value.</summary>
public sealed record MoraleSignalRow(string Signal, string Value);

/// <summary>The per-agent view. Display only: <see cref="Agents"/> is in display-name order and <see cref="Framing"/> is shown above it.</summary>
public sealed record AgentViewRow(string Visibility, string Framing, IReadOnlyList<AgentRow> Agents);

/// <summary>One agent's metrics, under its display name.</summary>
public sealed record AgentRow(string AgentId, string DisplayName, IReadOnlyList<AgentMetricRow> Metrics);

/// <summary>One agent metric as text.</summary>
public sealed record AgentMetricRow(string Metric, string Value);
