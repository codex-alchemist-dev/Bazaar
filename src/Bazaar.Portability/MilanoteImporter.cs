// Source docs (checked 2026-11-15):
//   https://help.milanote.com/en/articles/111395-exporting-your-work
// The article lists PDF (Standard and Large), PNG, Word, Markdown and plain text exports, plus a
// "Generate ZIP" action that packages a board's images and files. It documents no JSON or CSV board
// export and publishes no schema for any file, so no field names are taken from it. The item list
// names ("items", "cards", "notes") and the keys read below (type, title, name, text, content, milanote)
// are kept defensive and are NOT vendor-documented. Do not add fields without a documented source.
using System.Globalization;
using System.Text.Json;

namespace Bazaar.Portability;

/// <summary>
/// Milanote board export (JSON, unofficial). Milanote publishes no JSON export schema, so this importer is defensive:
/// it accepts a root array, or an object holding an "items", "cards" or "notes" array, and reads the
/// first string of title, name, text or content from each item. Non-object items yield empty records.
/// </summary>
public sealed class MilanoteImporter : IPortabilityImporter
{
    private const int _titleLimit = 80;
    private const string _marker = "milanote";
    private static readonly string[] _itemListNames = ["items", "cards", "notes"];

    public string Id => "bazaar.import.milanote";
    public string Version => "1.0.0";

    public DetectResult Detect(ImportSource source)
    {
        try
        {
            using var doc = JsonDocument.Parse(source.Content);
            if (!TryGetItems(doc.RootElement, out var items)) return new DetectResult(0, "no milanote item list");

            var nameHasMarker = source.Name.Contains(_marker, StringComparison.OrdinalIgnoreCase);
            var itemHasMarker = false;
            foreach (var item in items.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.Object && item.TryGetProperty(_marker, out _))
                {
                    itemHasMarker = true;
                    break;
                }
            }

            return itemHasMarker || nameHasMarker
                ? new DetectResult(0.8, "milanote marker")
                : new DetectResult(0.4, "item list shape only");
        }
        catch (JsonException)
        {
            return new DetectResult(0, "not JSON");
        }
    }

    public IReadOnlyList<SourceRecord> Parse(ImportSource source)
    {
        try
        {
            using var doc = JsonDocument.Parse(source.Content);
            if (!TryGetItems(doc.RootElement, out var items)) return [];

            var records = new List<SourceRecord>();
            var index = 0;
            foreach (var item in items.EnumerateArray())
            {
                // Body is the item's text or content; a card with neither falls back to its title or name.
                var explicitTitle = FirstString(item, "title", "name");
                var text = FirstString(item, "text", "content") ?? explicitTitle ?? string.Empty;
                var kind = FirstString(item, "type") ?? "note";
                var title = explicitTitle ?? Truncate(text);
                var key = SourceKeys.For(source.Name + "#" + index.ToString(CultureInfo.InvariantCulture), text);
                records.Add(new SourceRecord(
                    key,
                    kind,
                    title,
                    text,
                    new Dictionary<string, string>
                    {
                        ["format"] = "milanote",
                        ["source"] = source.Name,
                        ["type"] = kind,
                    }));
                index++;
            }
            return records;
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static bool TryGetItems(JsonElement root, out JsonElement items)
    {
        if (root.ValueKind == JsonValueKind.Array)
        {
            items = root;
            return true;
        }
        if (root.ValueKind == JsonValueKind.Object)
        {
            foreach (var name in _itemListNames)
            {
                if (root.TryGetProperty(name, out var list) && list.ValueKind == JsonValueKind.Array)
                {
                    items = list;
                    return true;
                }
            }
        }
        items = default;
        return false;
    }

    /// <summary>First non-blank string property among the given names; null for non-objects or when none match.</summary>
    private static string? FirstString(JsonElement item, params string[] names)
    {
        if (item.ValueKind != JsonValueKind.Object) return null;
        foreach (var name in names)
        {
            if (item.TryGetProperty(name, out var value)
                && value.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(value.GetString()))
            {
                return value.GetString();
            }
        }
        return null;
    }

    private static string Truncate(string text) =>
        text.Length <= _titleLimit ? text : text[.._titleLimit];
}
