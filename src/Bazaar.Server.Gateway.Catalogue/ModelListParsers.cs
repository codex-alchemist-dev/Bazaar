using System.Text.Json;

namespace Bazaar.Server.Gateway.Catalogue;

/// <summary>One page of a provider's model list: the ids, and the cursor to ask for the next page (null on the last page).</summary>
public sealed record ModelListPage(IReadOnlyList<string> Ids, string? NextCursor);

/// <summary>
/// Page readers for the two native list endpoints. Anthropic: <c>GET /v1/models</c> answers <c>{data:[{id}], has_more, last_id}</c>
/// and pages with <c>after_id</c>. Gemini: <c>GET /models</c> answers <c>{models:[{name:"models/x"}], nextPageToken}</c> and pages with
/// <c>pageToken</c>; the <c>models/</c> prefix is removed because the gateway names a Gemini model without it. Neither reader throws:
/// a body of the wrong shape yields <c>false</c>.
/// </summary>
public static class ModelListParsers
{
    private const string _geminiPrefix = "models/";

    public static bool TryParseAnthropic(string json, out ModelListPage page)
    {
        page = new ModelListPage([], null);
        if (!TryRoot(json, "data", out var data, out var root))
        {
            return false;
        }

        var ids = Collect(data, "id", stripPrefix: false);
        var hasMore = root.TryGetProperty("has_more", out var more) && more.ValueKind == JsonValueKind.True;
        string? last = root.TryGetProperty("last_id", out var lastId) && lastId.ValueKind == JsonValueKind.String ? lastId.GetString() : null;
        page = new ModelListPage(ids, hasMore && !string.IsNullOrEmpty(last) ? last : null);
        return true;
    }

    public static bool TryParseGemini(string json, out ModelListPage page)
    {
        page = new ModelListPage([], null);
        if (!TryRoot(json, "models", out var models, out var root))
        {
            return false;
        }

        var ids = Collect(models, "name", stripPrefix: true);
        string? next = root.TryGetProperty("nextPageToken", out var token) && token.ValueKind == JsonValueKind.String ? token.GetString() : null;
        page = new ModelListPage(ids, string.IsNullOrEmpty(next) ? null : next);
        return true;
    }

    private static bool TryRoot(string json, string arrayName, out JsonElement array, out JsonElement root)
    {
        array = default;
        root = default;
        if (string.IsNullOrWhiteSpace(json))
        {
            return false;
        }

        try
        {
            // The document is cloned so the elements outlive the parse scope.
            using var document = JsonDocument.Parse(json);
            root = document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return false;
        }

        return root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty(arrayName, out array)
            && array.ValueKind == JsonValueKind.Array;
    }

    private static List<string> Collect(JsonElement array, string property, bool stripPrefix)
    {
        var ids = new List<string>(array.GetArrayLength());
        foreach (var row in array.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Object
                || !row.TryGetProperty(property, out var value)
                || value.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var text = value.GetString();
            if (stripPrefix && text is not null && text.StartsWith(_geminiPrefix, StringComparison.Ordinal))
            {
                text = text[_geminiPrefix.Length..];
            }

            if (!string.IsNullOrWhiteSpace(text))
            {
                ids.Add(text);
            }
        }

        return ids;
    }
}
