using System.Reflection;
using Bazaar.Client.RyuseiFuda.Orrery;
using Bazaar.Client.RyuseiFuda.Panes;

namespace Bazaar.Client.RyuseiFuda;

/// <summary>
/// Data for the schedule view document <c>bazaar.ryusei.schedule</c> (doc 41c). The document declares the layout; this
/// adapter turns the pane view model into the data it binds to. Pause and Resume only show for a row whose state allows
/// them, and only when a head supplied the trigger actions (otherwise the row shows its state and no button).
/// </summary>
public static class RyuseiScheduleViewData
{
    /// <summary>Resource name of the embedded view document.</summary>
    public const string ViewResource = "Bazaar.Client.RyuseiFuda.Views.ryusei-schedule.view.json";

    /// <summary>The command ids the document invokes. The head maps them to <see cref="IRyuseiTriggerActions"/>.</summary>
    public const string PauseCommand = "ryusei.trigger.pause";

    public const string ResumeCommand = "ryusei.trigger.resume";

    /// <summary>Reads the embedded view document.</summary>
    public static string ReadViewJson()
    {
        using Stream stream = typeof(RyuseiScheduleViewData).Assembly.GetManifestResourceStream(ViewResource)
            ?? throw new InvalidOperationException($"embedded view '{ViewResource}' is missing.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>The data dictionary for the view, built from the pane's current rows.</summary>
    public static IReadOnlyDictionary<string, object?> From(RyuseiSchedulePaneViewModel pane)
    {
        ArgumentNullException.ThrowIfNull(pane);

        var rows = new List<IReadOnlyDictionary<string, object?>>();
        foreach (RyuseiMicroRow row in pane.Schedule.Rows)
        {
            bool active = string.Equals(row.State, "active", StringComparison.OrdinalIgnoreCase);
            bool paused = string.Equals(row.State, "paused", StringComparison.OrdinalIgnoreCase);
            rows.Add(new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["id"] = row.Id,
                ["summary"] = $"{row.KindLabel} · {row.StateLabel}",
                ["purpose"] = row.Purpose,
                ["detail"] = $"{row.NextFireText} · {row.TargetsText}",
                ["canPause"] = active && pane.CanChangeTriggers,
                ["canResume"] = paused && pane.CanChangeTriggers,
            });
        }

        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["triggers"] = rows,
            ["emptyText"] = "No triggers are scheduled.",
            ["emptyVisible"] = rows.Count == 0,
            ["errorText"] = pane.LastError ?? string.Empty,
            ["errorVisible"] = pane.LastError is not null,
        };
    }
}
