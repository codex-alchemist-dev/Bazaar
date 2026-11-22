using Bazaar.Client.RyuseiFuda.Panes;

namespace Bazaar.Client.RyuseiFuda;

/// <summary>
/// Data for the roadmap view document <c>bazaar.ryusei.roadmap</c> (doc 41c). The pane view model has already formatted the
/// text; this adapter flattens phases and objectives into one keyed list of lines, each a heading or an objective.
/// </summary>
public static class RyuseiRoadmapViewData
{
    /// <summary>Resource name of the embedded view document.</summary>
    public const string ViewResource = "Bazaar.Client.RyuseiFuda.Views.ryusei-roadmap.view.json";

    /// <summary>Reads the embedded view document.</summary>
    public static string ReadViewJson()
    {
        using Stream stream = typeof(RyuseiRoadmapViewData).Assembly.GetManifestResourceStream(ViewResource)
            ?? throw new InvalidOperationException($"embedded view '{ViewResource}' is missing.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>The data dictionary for the view, built from the pane's phases, unphased objectives and cycle.</summary>
    public static IReadOnlyDictionary<string, object?> From(RoadmapPaneViewModel pane)
    {
        ArgumentNullException.ThrowIfNull(pane);

        var lines = new List<IReadOnlyDictionary<string, object?>>();
        void Add(string text, bool heading) => lines.Add(new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["id"] = lines.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["text"] = text,
            ["heading"] = heading,
        });

        foreach (RoadmapPhaseRow phase in pane.Phases)
        {
            Add($"{phase.Title} · {phase.Gate}", heading: true);
            foreach (RoadmapObjectiveRow objective in phase.Objectives)
            {
                Add(objective.Line, heading: false);
            }
        }

        if (pane.UnphasedObjectives.Count > 0)
        {
            Add("Unphased", heading: true);
            foreach (RoadmapObjectiveRow objective in pane.UnphasedObjectives)
            {
                Add(objective.Line, heading: false);
            }
        }

        bool cycle = pane.DependencyCycle.Count > 0;
        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["lines"] = lines,
            ["cycleText"] = cycle ? "Dependency cycle: " + string.Join(" → ", pane.DependencyCycle) : string.Empty,
            ["cycleVisible"] = cycle,
            ["emptyText"] = "No objectives yet.",
            ["emptyVisible"] = lines.Count == 0,
        };
    }
}
