using System.Text.Json;

namespace Bazaar.Server.Gateway.Catalogue;

/// <summary>
/// Reads an OpenAI-style <c>GET /models</c> body: an object with a <c>data</c> array whose rows carry a string <c>id</c>.
/// Rows without a usable id are skipped. Never throws: a body that is not that shape yields <c>false</c>.
/// </summary>
public static class OpenAiModelListParser
{
    public static bool TryParse(string json, out IReadOnlyList<string> ids)
    {
        ids = [];
        if (string.IsNullOrWhiteSpace(json))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("data", out var data)
                || data.ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            var list = new List<string>(data.GetArrayLength());
            foreach (var row in data.EnumerateArray())
            {
                if (row.ValueKind == JsonValueKind.Object
                    && row.TryGetProperty("id", out var id)
                    && id.ValueKind == JsonValueKind.String
                    && !string.IsNullOrWhiteSpace(id.GetString()))
                {
                    list.Add(id.GetString()!);
                }
            }

            ids = list;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
