using Bazaar.Client.RyuseiFuda.Panes;

namespace Bazaar.Client.RyuseiFuda;

/// <summary>
/// Data for the workday view document <c>bazaar.ryusei.workday</c> (doc 41c): the team schedule grouped by owner, flattened
/// into one keyed list of lines, each an owner heading or a task. The server's order is kept.
/// </summary>
public static class RyuseiWorkdayViewData
{
    /// <summary>Resource name of the embedded view document.</summary>
    public const string ViewResource = "Bazaar.Client.RyuseiFuda.Views.ryusei-workday.view.json";

    /// <summary>Reads the embedded view document.</summary>
    public static string ReadViewJson()
    {
        using Stream stream = typeof(RyuseiWorkdayViewData).Assembly.GetManifestResourceStream(ViewResource)
            ?? throw new InvalidOperationException($"embedded view '{ViewResource}' is missing.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>The data dictionary for the view, built from the pane's owners and tasks.</summary>
    public static IReadOnlyDictionary<string, object?> From(WorkdayPaneViewModel pane)
    {
        ArgumentNullException.ThrowIfNull(pane);

        var lines = new List<IReadOnlyDictionary<string, object?>>();
        void Add(string text, bool heading) => lines.Add(new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["id"] = lines.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["text"] = text,
            ["heading"] = heading,
        });

        foreach (WorkdayOwnerRow owner in pane.Owners)
        {
            Add($"{owner.Name} ({owner.Tasks.Count})", heading: true);
            foreach (WorkdayTaskRow task in owner.Tasks)
            {
                Add(task.Line, heading: false);
            }
        }

        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["lines"] = lines,
            ["emptyText"] = "No tasks on the board.",
            ["emptyVisible"] = lines.Count == 0,
        };
    }
}
