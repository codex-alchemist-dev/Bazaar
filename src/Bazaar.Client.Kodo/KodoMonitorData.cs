namespace Bazaar.Client.Kodo;

/// <summary>
/// Turns the Monitor header row into the data the <c>bazaar.kodo.monitor</c> view document declares (doc 46, sections 8.1,
/// 12.1 and 14; doc 41c). It copies; it decides nothing. A stale snapshot is shown as <c>unobserved</c> (doc 46, section 8.3),
/// because the client knows only that it has not heard from the server, not what the state is.
/// </summary>
public static class KodoMonitorData
{
    /// <summary>The embedded view document's resource name.</summary>
    public const string ViewResource = "Bazaar.Client.Kodo.Views.kodo-monitor.view.json";

    /// <summary>The view document as JSON text.</summary>
    public static string ReadViewJson()
    {
        using Stream stream = typeof(KodoMonitorData).Assembly.GetManifestResourceStream(ViewResource)
            ?? throw new InvalidOperationException($"Missing embedded resource '{ViewResource}'.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>The data for one render. Every key the view declares is present, so a missing control reads as hidden.</summary>
    public static IReadOnlyDictionary<string, object?> From(KodoStatusRow row)
    {
        ArgumentNullException.ThrowIfNull(row);

        var data = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["state"] = row.IsStale ? "unobserved" : row.State.ToString().ToLowerInvariant(),
            ["headline"] = row.IsStale ? "Unobserved" : row.Headline,
            ["scoreText"] = row.ScoreText,
            ["freshnessText"] = row.FreshnessText,
        };

        foreach (KodoControl control in Enum.GetValues<KodoControl>())
        {
            string key = KeyOf(control);
            KodoControlRow? shown = row.Controls.FirstOrDefault(c => c.Control == control);
            data[key + "Visible"] = shown is not null;
            data[key + "Enabled"] = shown?.Enabled ?? false;
            data[key + "Reason"] = shown?.DisabledReason ?? string.Empty;
        }

        return data;
    }

    private static string KeyOf(KodoControl control) => control switch
    {
        KodoControl.Restart => "restart",
        KodoControl.Isolate => "isolate",
        KodoControl.RollBack => "rollBack",
        KodoControl.IncidentExit => "incidentExit",
        _ => throw new ArgumentOutOfRangeException(nameof(control), control, null),
    };
}
