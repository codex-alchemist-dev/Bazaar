using System.Globalization;
using System.Text.Json;

namespace Bazaar.Portability;

/// <summary>
/// Trello board export (JSON). One record per card, including closed cards (marked by the "closed" attribute).
/// The card's list name and due date are written as leading body lines, then the card description.
/// </summary>
public sealed class TrelloImporter : IPortabilityImporter
{
    public string Id => "bazaar.import.trello";
    public string Version => "1.0.0";

    public DetectResult Detect(ImportSource source)
    {
        if (source.Content.Length == 0) return new DetectResult(0, "empty");
        try
        {
            using var doc = JsonDocument.Parse(source.Content);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return new DetectResult(0, "not a JSON object");
            if (Str(root, "name") is null) return new DetectResult(0, "no board name");
            if (!HasKind(root, "lists", JsonValueKind.Array) || !HasKind(root, "cards", JsonValueKind.Array))
                return new DetectResult(0, "no lists or cards arrays");
            return new DetectResult(0.95, "Trello board export");
        }
        catch (JsonException)
        {
            return new DetectResult(0, "not JSON");
        }
    }

    public IReadOnlyList<SourceRecord> Parse(ImportSource source)
    {
        using var doc = JsonDocument.Parse(source.Content);
        var root = doc.RootElement;
        var board = Str(root, "name") ?? "";

        var listNames = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var list in root.GetProperty("lists").EnumerateArray())
        {
            var listId = Str(list, "id");
            var listName = Str(list, "name");
            if (listId is not null && listName is not null) listNames[listId] = listName;
        }

        var records = new List<SourceRecord>();
        var index = 0;
        foreach (var card in root.GetProperty("cards").EnumerateArray())
        {
            var cardId = Str(card, "id") ?? index.ToString(CultureInfo.InvariantCulture);
            index++;

            var idList = Str(card, "idList");
            var listName = idList is not null && listNames.TryGetValue(idList, out var resolved) ? resolved : "unknown";
            var due = Str(card, "due");
            var desc = (Str(card, "desc") ?? "").Replace("\r\n", "\n");

            var lines = new List<string> { "List: " + listName };
            if (!string.IsNullOrEmpty(due)) lines.Add("Due: " + due);
            if (desc.Length > 0) lines.Add(desc);
            var body = string.Join('\n', lines);

            var attributes = new Dictionary<string, string>
            {
                ["format"] = "trello",
                ["list"] = listName,
                ["closed"] = HasTrue(card, "closed") ? "true" : "false",
                ["source"] = source.Name,
                ["board"] = board,
            };
            var labels = LabelNames(card);
            if (labels.Count > 0) attributes["labels"] = string.Join(",", labels);

            records.Add(new SourceRecord(
                SourceKeys.For(source.Name + "#" + cardId, body),
                "card",
                Str(card, "name") ?? "",
                body,
                attributes));
        }
        return records;
    }

    private static string? Str(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;
        if (!element.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String) return null;
        return value.GetString();
    }

    private static bool HasKind(JsonElement element, string name, JsonValueKind kind) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == kind;

    private static bool HasTrue(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    private static List<string> LabelNames(JsonElement card)
    {
        var names = new List<string>();
        if (!card.TryGetProperty("labels", out var labels) || labels.ValueKind != JsonValueKind.Array) return names;
        foreach (var label in labels.EnumerateArray())
        {
            var name = Str(label, "name");
            if (!string.IsNullOrWhiteSpace(name)) names.Add(name);
        }
        return names;
    }
}
