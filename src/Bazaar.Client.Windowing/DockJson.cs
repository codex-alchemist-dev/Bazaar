using System.Text;
using System.Text.Json;

namespace Bazaar.Client.Windowing;

/// <summary>
/// Compact JSON form of a dock layout, written with <see cref="Utf8JsonWriter"/> and read with <see cref="JsonDocument"/>.
/// No reflection, so it is AOT safe.
/// </summary>
/// <remarks>
/// Format: a pane is <c>{"t":"pane","kind":"...","id":"..."}</c>, a tab group is
/// <c>{"t":"tabs","active":0,"tabs":[pane,...]}</c>, and a split is
/// <c>{"t":"split","o":"h"|"v","r":0.5,"a":node,"b":node}</c>. A null layout is the literal <c>null</c>.
/// </remarks>
public static class DockJson
{
    /// <summary>Serializes a layout. Output is deterministic. A null layout gives <c>null</c>.</summary>
    public static string Serialize(DockNode? root)
    {
        if (root is null)
        {
            return "null";
        }

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            WriteNode(writer, root);
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>
    /// Parses a layout. Never throws: bad JSON, an unknown node type, an empty tab group, a missing or
    /// wrongly typed field, a duplicate pane id, or a depth above <see cref="DockOps.MaxDepth"/> gives null.
    /// Ratios are clamped and active indexes are clamped to their group on read.
    /// </summary>
    public static DockNode? Deserialize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var paneIds = new HashSet<string>(StringComparer.Ordinal);
            return ReadNode(document.RootElement, 1, paneIds);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static void WriteNode(Utf8JsonWriter writer, DockNode node)
    {
        switch (node)
        {
            case DockPane pane:
                WritePane(writer, pane);
                break;

            case DockTabs tabs:
                writer.WriteStartObject();
                writer.WriteString("t", "tabs");
                writer.WriteNumber("active", tabs.ActiveIndex);
                writer.WriteStartArray("tabs");
                foreach (var pane in tabs.Tabs)
                {
                    WritePane(writer, pane);
                }

                writer.WriteEndArray();
                writer.WriteEndObject();
                break;

            case DockSplit split:
                writer.WriteStartObject();
                writer.WriteString("t", "split");
                writer.WriteString("o", split.Orientation == DockOrientation.Horizontal ? "h" : "v");
                writer.WriteNumber("r", split.Ratio);
                writer.WritePropertyName("a");
                WriteNode(writer, split.First);
                writer.WritePropertyName("b");
                WriteNode(writer, split.Second);
                writer.WriteEndObject();
                break;

            default:
                throw new ArgumentException("Unknown dock node type.", nameof(node));
        }
    }

    private static void WritePane(Utf8JsonWriter writer, DockPane pane)
    {
        writer.WriteStartObject();
        writer.WriteString("t", "pane");
        writer.WriteString("kind", pane.PaneKind);
        writer.WriteString("id", pane.PaneId);
        writer.WriteEndObject();
    }

    private static DockNode? ReadNode(JsonElement element, int depth, HashSet<string> paneIds)
    {
        if (depth > DockOps.MaxDepth || element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (!TryGetString(element, "t", out var type))
        {
            return null;
        }

        return type switch
        {
            "pane" => ReadPane(element, paneIds),
            "tabs" => ReadTabs(element, paneIds),
            "split" => ReadSplit(element, depth, paneIds),
            _ => null,
        };
    }

    private static DockPane? ReadPane(JsonElement element, HashSet<string> paneIds)
    {
        if (!TryGetString(element, "t", out var type) || type != "pane")
        {
            return null;
        }

        if (!TryGetString(element, "kind", out var kind) || !TryGetString(element, "id", out var id))
        {
            return null;
        }

        if (kind.Length == 0 || id.Length == 0 || !paneIds.Add(id))
        {
            return null;
        }

        return new DockPane(kind, id);
    }

    private static DockTabs? ReadTabs(JsonElement element, HashSet<string> paneIds)
    {
        if (!element.TryGetProperty("tabs", out var list)
            || list.ValueKind != JsonValueKind.Array
            || !TryGetNumber(element, "active", out var active))
        {
            return null;
        }

        var panes = new List<DockPane>();
        foreach (var item in list.EnumerateArray())
        {
            var pane = ReadPane(item, paneIds);
            if (pane is null)
            {
                return null;
            }

            panes.Add(pane);
        }

        if (panes.Count == 0)
        {
            return null;
        }

        var index = (int)Math.Clamp(Math.Floor(active), 0, panes.Count - 1);
        return new DockTabs(panes, index);
    }

    private static DockSplit? ReadSplit(JsonElement element, int depth, HashSet<string> paneIds)
    {
        if (!TryGetString(element, "o", out var orientation)
            || (orientation != "h" && orientation != "v")
            || !TryGetNumber(element, "r", out var ratio)
            || !element.TryGetProperty("a", out var a)
            || !element.TryGetProperty("b", out var b))
        {
            return null;
        }

        var first = ReadNode(a, depth + 1, paneIds);
        if (first is null)
        {
            return null;
        }

        var second = ReadNode(b, depth + 1, paneIds);
        if (second is null)
        {
            return null;
        }

        var direction = orientation == "h" ? DockOrientation.Horizontal : DockOrientation.Vertical;
        return new DockSplit(direction, DockOps.ClampRatio(ratio), first, second);
    }

    private static bool TryGetString(JsonElement element, string name, out string value)
    {
        value = string.Empty;
        if (element.ValueKind != JsonValueKind.Object
            || !element.TryGetProperty(name, out var property)
            || property.ValueKind != JsonValueKind.String
            || property.GetString() is not { } text)
        {
            return false;
        }

        value = text;
        return true;
    }

    private static bool TryGetNumber(JsonElement element, string name, out double value)
    {
        value = 0;
        if (element.ValueKind != JsonValueKind.Object
            || !element.TryGetProperty(name, out var property)
            || property.ValueKind != JsonValueKind.Number)
        {
            return false;
        }

        return property.TryGetDouble(out value);
    }
}
