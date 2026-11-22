using System.Text.Json;

namespace Bazaar.Server.Portability;

/// <summary>
/// Writes stored JSON documents (event payloads, trigger specs, fire causes) in one canonical shape: object properties in
/// ordinal order, array order kept, numbers as their stored text. The same document always gives the same bytes, whatever key
/// order the database returned (doc 44 section 4.1).
/// </summary>
internal static class CanonicalJson
{
    /// <summary>Writes <paramref name="name"/> with the canonical form of <paramref name="jsonText"/>, or null when the text is null.</summary>
    internal static void WriteProperty(Utf8JsonWriter json, string name, string? jsonText)
    {
        json.WritePropertyName(name);
        if (jsonText is null)
        {
            json.WriteNullValue();
            return;
        }

        using var document = JsonDocument.Parse(jsonText);
        WriteValue(json, document.RootElement);
    }

    private static void WriteValue(Utf8JsonWriter json, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                json.WriteStartObject();
                foreach (var property in element.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
                {
                    json.WritePropertyName(property.Name);
                    WriteValue(json, property.Value);
                }

                json.WriteEndObject();
                break;
            case JsonValueKind.Array:
                json.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                {
                    WriteValue(json, item);
                }

                json.WriteEndArray();
                break;
            case JsonValueKind.String:
                json.WriteStringValue(element.GetString());
                break;
            case JsonValueKind.Number:
                json.WriteRawValue(element.GetRawText());
                break;
            case JsonValueKind.True:
                json.WriteBooleanValue(true);
                break;
            case JsonValueKind.False:
                json.WriteBooleanValue(false);
                break;
            default:
                json.WriteNullValue();
                break;
        }
    }
}
