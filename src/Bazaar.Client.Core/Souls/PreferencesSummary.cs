using System.Text;
using System.Text.Json;

namespace Bazaar.Client.Core.Souls;

/// <summary>
/// Turns a Soul's canonical preferences JSON into a one-line summary for the profile card. Pure: no I/O.
/// Keys are sorted, nested values are shown by size, and the list is capped so the card stays short.
/// </summary>
public static class PreferencesSummary
{
    /// <summary>The most key/value pairs shown before the rest are counted as "+N more".</summary>
    public const int MaxShownEntries = 5;

    /// <summary>The longest value text shown before it is cut with an ellipsis.</summary>
    public const int MaxValueLength = 40;

    /// <summary>Shown when the Soul has no preferences recorded at all.</summary>
    public const string NoneRecorded = "No preferences recorded";

    /// <summary>Shown when the stored preferences cannot be parsed.</summary>
    public const string Unreadable = "Preferences unreadable";

    /// <summary>
    /// Summarises preferences JSON.
    /// </summary>
    /// <param name="preferencesJson">Preferences as JSON text, or null when the Soul has no version.</param>
    /// <returns>A one-line summary.</returns>
    public static string Format(string? preferencesJson)
    {
        if (string.IsNullOrWhiteSpace(preferencesJson))
        {
            return NoneRecorded;
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(preferencesJson);
        }
        catch (JsonException)
        {
            return Unreadable;
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return Unreadable;
            }

            var entries = document.RootElement.EnumerateObject()
                .OrderBy(property => property.Name, StringComparer.Ordinal)
                .ToList();

            if (entries.Count == 0)
            {
                return NoneRecorded;
            }

            var shown = entries
                .Take(MaxShownEntries)
                .Select(property => property.Name + ": " + DescribeValue(property.Value));

            var text = new StringBuilder(string.Join(", ", shown));
            if (entries.Count > MaxShownEntries)
            {
                text.Append(", +").Append(entries.Count - MaxShownEntries).Append(" more");
            }

            return text.ToString();
        }
    }

    private static string DescribeValue(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => Truncate(value.GetString() ?? string.Empty),
        JsonValueKind.Number => value.GetRawText(),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        JsonValueKind.Null => "null",
        JsonValueKind.Object => "{" + value.EnumerateObject().Count() + " keys}",
        JsonValueKind.Array => "[" + value.GetArrayLength() + " items]",
        _ => "?",
    };

    private static string Truncate(string text) =>
        text.Length <= MaxValueLength ? text : string.Concat(text.AsSpan(0, MaxValueLength - 1), "…");
}
