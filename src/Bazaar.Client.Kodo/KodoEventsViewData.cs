using System.Globalization;

namespace Bazaar.Client.Kodo;

/// <summary>
/// Turns journal events into the data the <c>bazaar.kodo.events</c> view document declares (doc 46, section 14.2, view 8; doc
/// 41c). It copies and formats; it decides nothing. The proof button invokes <see cref="ProofCommand"/> with the record id, and
/// the head fetches the Merkle proof from the server (<c>GET /journal/proof/{recordId}</c>).
/// </summary>
public static class KodoEventsViewData
{
    /// <summary>The embedded view document's resource name.</summary>
    public const string ViewResource = "Bazaar.Client.Kodo.Views.kodo-events.view.json";

    /// <summary>The command the proof button invokes, with one text argument <c>id</c>: the record id.</summary>
    public const string ProofCommand = "kodo.journal.proof";

    /// <summary>The view document as JSON text.</summary>
    public static string ReadViewJson()
    {
        using Stream stream = typeof(KodoEventsViewData).Assembly.GetManifestResourceStream(ViewResource)
            ?? throw new InvalidOperationException($"Missing embedded resource '{ViewResource}'.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>The data for one render, newest event first. An empty list shows the empty line.</summary>
    public static IReadOnlyDictionary<string, object?> From(IReadOnlyList<KodoEventRow> events, string emptyText)
    {
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(emptyText);

        var rows = events
            .OrderByDescending(e => e.At)
            .ThenByDescending(e => e.ChainPosition)
            .Select(e => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["id"] = e.RecordId,
                ["kind"] = e.Kind == KodoEventKind.Update ? "Update" : "Audit",
                ["summary"] = e.Summary,
                ["when"] = e.At.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture),
                ["position"] = "#" + e.ChainPosition.ToString(CultureInfo.InvariantCulture),
            })
            .ToList();

        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["events"] = rows,
            ["emptyText"] = emptyText,
            ["emptyVisible"] = rows.Count == 0,
        };
    }
}
