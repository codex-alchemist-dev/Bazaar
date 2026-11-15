using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Bazaar.Portability;

/// <summary>
/// Miro board export (JSON). The export schema is assumed, not specified: a root object whose "data",
/// "items" or "nodes" array holds objects with a "type" and text in "data" or "content". Frame titles may
/// also come from a top-level "frames" array. Anything unreadable is skipped rather than failing the source.
/// </summary>
public sealed class MiroImporter : IPortabilityImporter
{
    private const int _titleLength = 80;
    private static readonly string[] _itemArrayNames = ["data", "items", "nodes"];
    private static readonly string[] _textPropertyNames = ["data", "content"];
    private static readonly HashSet<string> _knownTypes = new(StringComparer.Ordinal)
    {
        "sticky_note", "text", "shape", "frame",
    };

    public string Id => "bazaar.import.miro";
    public string Version => "1.0.0";

    public DetectResult Detect(ImportSource source)
    {
        try
        {
            using var document = JsonDocument.Parse(source.Content);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return new DetectResult(0, "not a JSON object");
            var items = FindItems(root);
            if (items is null) return new DetectResult(0, "no data, items or nodes array");
            if (source.Name.Contains("miro", StringComparison.OrdinalIgnoreCase))
                return new DetectResult(0.8, "miro file name");
            foreach (var item in items.Value.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.Object
                    && item.TryGetProperty("type", out var type)
                    && type.ValueKind == JsonValueKind.String
                    && type.GetString() is { } kind
                    && _knownTypes.Contains(kind))
                {
                    return new DetectResult(0.5, "miro item types");
                }
            }
            return new DetectResult(0, "no miro markers");
        }
        catch (JsonException)
        {
            return new DetectResult(0, "not JSON");
        }
    }

    public IReadOnlyList<SourceRecord> Parse(ImportSource source)
    {
        var records = new List<SourceRecord>();
        try
        {
            using var document = JsonDocument.Parse(source.Content);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return records;

            var items = FindItems(root);
            if (items is not null)
            {
                var index = 0;
                foreach (var item in items.Value.EnumerateArray())
                {
                    var position = index++;
                    if (item.ValueKind != JsonValueKind.Object) continue;
                    var kind = item.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String
                        ? type.GetString()
                        : null;
                    AddRecord(records, source, position.ToString(CultureInfo.InvariantCulture), kind ?? "item", ExtractText(item));
                }
            }

            if (root.TryGetProperty("frames", out var frames) && frames.ValueKind == JsonValueKind.Array)
            {
                var index = 0;
                foreach (var frame in frames.EnumerateArray())
                {
                    var position = index++;
                    AddRecord(records, source, "frames/" + position.ToString(CultureInfo.InvariantCulture), "frame", ExtractFrameTitle(frame));
                }
            }
        }
        catch (JsonException)
        {
            return records;
        }
        return records;
    }

    private static JsonElement? FindItems(JsonElement root)
    {
        foreach (var name in _itemArrayNames)
        {
            if (root.TryGetProperty(name, out var array) && array.ValueKind == JsonValueKind.Array) return array;
        }
        return null;
    }

    private static string? ExtractText(JsonElement item)
    {
        foreach (var name in _textPropertyNames)
        {
            if (!item.TryGetProperty(name, out var value)) continue;
            if (value.ValueKind == JsonValueKind.String) return value.GetString();
            if (value.ValueKind == JsonValueKind.Object
                && value.TryGetProperty("content", out var inner)
                && inner.ValueKind == JsonValueKind.String)
            {
                return inner.GetString();
            }
        }
        return null;
    }

    private static string? ExtractFrameTitle(JsonElement frame)
    {
        if (frame.ValueKind == JsonValueKind.String) return frame.GetString();
        if (frame.ValueKind != JsonValueKind.Object) return null;
        if (frame.TryGetProperty("title", out var title) && title.ValueKind == JsonValueKind.String) return title.GetString();
        return ExtractText(frame);
    }

    private static void AddRecord(List<SourceRecord> records, ImportSource source, string position, string kind, string? raw)
    {
        if (raw is null) return;
        var body = StripTags(raw);
        if (body.Length == 0) return;
        records.Add(new SourceRecord(
            SourceKeys.For(source.Name + "#" + position, raw),
            kind,
            body.Length > _titleLength ? body[.._titleLength] : body,
            body,
            new Dictionary<string, string>
            {
                ["format"] = "miro",
                ["source"] = source.Name,
                ["type"] = kind,
            }));
    }

    /// <summary>Removes &lt;...&gt; spans. A '&lt;' with no closing '&gt;' stays as ordinary text.</summary>
    private static string StripTags(string text)
    {
        var builder = new StringBuilder(text.Length);
        var position = 0;
        while (position < text.Length)
        {
            var open = text.IndexOf('<', position);
            if (open < 0) break;
            var close = text.IndexOf('>', open + 1);
            if (close < 0) break;
            builder.Append(text, position, open - position);
            position = close + 1;
        }
        builder.Append(text, position, text.Length - position);
        return builder.ToString().Trim();
    }
}
