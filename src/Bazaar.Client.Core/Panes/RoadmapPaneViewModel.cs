using System.Globalization;
using System.Text.Json;
using Bazaar.Contracts.Panes;

namespace Bazaar.Client.Core.Panes;

/// <summary>
/// The objective roadmap pane (pane kind <see cref="PaneKinds.Roadmap"/>, doc 38 layer 3). The server derives the roadmap
/// and sends it as data; this view model only formats it. Model:
/// <c>{ "phases": [ { "slug", "title", "gatePassed", "objectives": [ objective ] } ], "unphasedObjectives": [ objective ],
/// "dependencyCycle": [ "slug", ... ] }</c>, where an objective is
/// <c>{ "path", "title", "status", "owner", "dependsOn": [...], "linkedTasks", "doneTasks", "doneShare" }</c>. A missing
/// required field raises <see cref="PaneModelException"/>, so the registry falls back to the generic view.
/// </summary>
public sealed class RoadmapPaneViewModel : IPaneViewModel
{
    private static readonly string[] _statuses = ["planned", "active", "done", "dropped"];

    public RoadmapPaneViewModel(PaneDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        Kind = descriptor.Kind;
        Title = "RyuseiFuda · Roadmap";

        var model = descriptor.Model;
        Phases = PaneValues.Array(model, "phases").EnumerateArray().Select(ReadPhase).ToArray();
        UnphasedObjectives = ReadOptionalObjectives(model, "unphasedObjectives");
        DependencyCycle = ReadOptionalStrings(model, "dependencyCycle");
    }

    public string Kind { get; }

    public string Title { get; }

    public IReadOnlyList<RoadmapPhaseRow> Phases { get; }

    public IReadOnlyList<RoadmapObjectiveRow> UnphasedObjectives { get; }

    /// <summary>Slugs on the cycle, in order, when the server found a dependency cycle; otherwise empty.</summary>
    public IReadOnlyList<string> DependencyCycle { get; }

    private static RoadmapPhaseRow ReadPhase(JsonElement phase)
    {
        if (phase.ValueKind != JsonValueKind.Object)
        {
            throw new PaneModelException("each phase must be an object");
        }

        var gate = PaneValues.RequiredBool(phase, "gatePassed") ? "gate passed" : "gate open";
        var objectives = ReadOptionalObjectives(phase, "objectives");
        return new RoadmapPhaseRow(PaneValues.RequiredString(phase, "title"), gate, objectives);
    }

    private static RoadmapObjectiveRow[] ReadOptionalObjectives(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out _))
        {
            return [];
        }

        return PaneValues.Array(parent, name).EnumerateArray().Select(ReadObjective).ToArray();
    }

    private static RoadmapObjectiveRow ReadObjective(JsonElement objective)
    {
        if (objective.ValueKind != JsonValueKind.Object)
        {
            throw new PaneModelException("each objective must be an object");
        }

        var status = PaneValues.RequiredString(objective, "status");
        if (Array.IndexOf(_statuses, status) < 0)
        {
            throw new PaneModelException($"unknown objective status '{status}'");
        }

        var linked = PaneValues.RequiredInt(objective, "linkedTasks");
        var done = PaneValues.RequiredInt(objective, "doneTasks");
        var progress = linked == 0
            ? "no linked tasks"
            : $"{done}/{linked} done" + (objective.TryGetProperty("doneShare", out var share) && share.ValueKind == JsonValueKind.Number
                ? $" ({Math.Round(share.GetDecimal() * 100m, 0, MidpointRounding.AwayFromZero).ToString("0", CultureInfo.InvariantCulture)}%)"
                : string.Empty);

        return new RoadmapObjectiveRow(
            PaneValues.RequiredString(objective, "title"),
            status,
            progress,
            ReadOptionalStrings(objective, "dependsOn"));
    }

    private static List<string> ReadOptionalStrings(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out _))
        {
            return [];
        }

        var result = new List<string>();
        foreach (var item in PaneValues.Array(parent, name).EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
            {
                throw new PaneModelException($"'{name}' must hold strings");
            }

            result.Add(item.GetString()!);
        }

        return result;
    }
}

/// <summary>One phase row: its title, gate text and objectives.</summary>
public sealed record RoadmapPhaseRow(string Title, string Gate, IReadOnlyList<RoadmapObjectiveRow> Objectives);

/// <summary>One objective row. <see cref="Line"/> is the text the view shows.</summary>
public sealed record RoadmapObjectiveRow(string Title, string Status, string Progress, IReadOnlyList<string> DependsOn)
{
    public string Line =>
        $"{Title} [{Status}] · {Progress}" + (DependsOn.Count > 0 ? $" · after {string.Join(", ", DependsOn)}" : string.Empty);
}
