using System.Text.Json;

namespace Bazaar.Client.Studio;

/// <summary>
/// Reads the open Studio ids out of a <c>list_roblox_studios</c> MCP result. The exact shape is not documented in doc 20, so this accepts
/// <c>structuredContent</c> or JSON text content holding an array, or an object with a <c>studios</c> array, whose items are
/// strings or objects with <c>studio_id</c> or <c>id</c>. Anything else yields no ids, which the relay treats as "no Studio open".
/// </summary>
internal static class StudioListing
{
    public static HashSet<string> ParseStudioIds(JsonElement result)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        if (result.ValueKind != JsonValueKind.Object)
        {
            return ids;
        }

        if (result.TryGetProperty("structuredContent", out var structured))
        {
            Collect(structured, ids);
        }

        if (ids.Count == 0 && result.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in content.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.Object
                    && item.TryGetProperty("text", out var text)
                    && text.ValueKind == JsonValueKind.String
                    && TryParse(text.GetString(), out var parsed))
                {
                    Collect(parsed, ids);
                }
            }
        }

        return ids;
    }

    private static bool TryParse(string? text, out JsonElement element)
    {
        element = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(text);
            element = doc.RootElement.Clone();
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static void Collect(JsonElement element, HashSet<string> ids)
    {
        if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty("studios", out var studios))
        {
            element = studios;
        }

        if (element.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var item in element.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String && item.GetString() is { Length: > 0 } s)
            {
                ids.Add(s);
            }
            else if (item.ValueKind == JsonValueKind.Object)
            {
                foreach (var name in new[] { "studio_id", "id" })
                {
                    if (item.TryGetProperty(name, out var id) && id.ValueKind == JsonValueKind.String && id.GetString() is { Length: > 0 } v)
                    {
                        ids.Add(v);
                        break;
                    }
                }
            }
        }
    }
}
