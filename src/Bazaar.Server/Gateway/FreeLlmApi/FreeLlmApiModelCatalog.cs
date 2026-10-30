using System.Text.Json;

namespace Bazaar.Server.Gateway.FreeLlmApi;

/// <summary>
/// Reads FreeLLMAPI's <c>GET /v1/models</c> body: a top-level object with a <c>data</c> array of rows, each with a
/// non-empty string <c>id</c> and an optional string <c>execution_status</c>. Unknown properties are ignored.
/// Never throws: any problem yields <c>false</c> and an empty list.
/// </summary>
public static class FreeLlmApiModelCatalog
{
    private const string _dataProperty = "data";
    private const string _idProperty = "id";
    private const string _statusProperty = "execution_status";

    public static bool TryParse(string json, out IReadOnlyList<FreeLlmApiModel> models)
    {
        models = [];
        if (string.IsNullOrWhiteSpace(json))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty(_dataProperty, out var data)
                || data.ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            var rows = new List<FreeLlmApiModel>(data.GetArrayLength());
            foreach (var row in data.EnumerateArray())
            {
                if (row.ValueKind != JsonValueKind.Object
                    || !row.TryGetProperty(_idProperty, out var id)
                    || id.ValueKind != JsonValueKind.String
                    || string.IsNullOrEmpty(id.GetString()))
                {
                    return false;
                }

                string? status = null;
                if (row.TryGetProperty(_statusProperty, out var statusElement) && statusElement.ValueKind == JsonValueKind.String)
                {
                    status = statusElement.GetString();
                }

                rows.Add(new FreeLlmApiModel(id.GetString()!, status));
            }

            models = rows;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
