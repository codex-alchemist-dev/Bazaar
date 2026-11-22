using System.Globalization;

namespace Bazaar.Client.Kodo;

/// <summary>
/// Turns open alerts into the data the <c>bazaar.kodo.alerts</c> view document declares (doc 46, sections 14.2 and 16.1; doc
/// 41c). It copies and formats; it decides nothing. The acknowledge button invokes <see cref="AckCommand"/> with the alert's
/// fingerprint, and the head posts <c>/alerts/{id}/ack</c>.
/// </summary>
public static class KodoAlertsViewData
{
    /// <summary>The embedded view document's resource name.</summary>
    public const string ViewResource = "Bazaar.Client.Kodo.Views.kodo-alerts.view.json";

    /// <summary>The command the acknowledge button invokes, with one text argument <c>id</c>: the alert fingerprint.</summary>
    public const string AckCommand = "kodo.alert.ack";

    /// <summary>The view document as JSON text.</summary>
    public static string ReadViewJson()
    {
        using Stream stream = typeof(KodoAlertsViewData).Assembly.GetManifestResourceStream(ViewResource)
            ?? throw new InvalidOperationException($"Missing embedded resource '{ViewResource}'.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>The data for one render: most severe first, then newest. An empty list shows the empty line.</summary>
    public static IReadOnlyDictionary<string, object?> From(IReadOnlyList<KodoAlertRow> alerts, string emptyText)
    {
        ArgumentNullException.ThrowIfNull(alerts);
        ArgumentNullException.ThrowIfNull(emptyText);

        var rows = alerts
            .OrderByDescending(a => a.Severity)
            .ThenByDescending(a => a.RaisedAt)
            .Select(a => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["id"] = a.Fingerprint,
                ["isCritical"] = a.Severity == KodoAlertSeverity.Critical,
                ["isWarning"] = a.Severity == KodoAlertSeverity.Warning,
                ["isInfo"] = a.Severity == KodoAlertSeverity.Info,
                ["severity"] = a.Severity.ToString(),
                ["rule"] = a.Rule,
                ["when"] = a.RaisedAt.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture),
                ["canAck"] = a.CanAcknowledge && !a.Acknowledged,
                ["ackedText"] = a.Acknowledged ? "Acknowledged" : string.Empty,
                ["ackedVisible"] = a.Acknowledged,
            })
            .ToList();

        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["alerts"] = rows,
            ["emptyText"] = emptyText,
            ["emptyVisible"] = rows.Count == 0,
        };
    }
}
