using System.Text;
using System.Text.Json;

namespace Bazaar.Server.Souls;

/// <summary>
/// Canonical JSON for Soul preferences: object keys sorted by ordinal comparison, no insignificant whitespace.
/// Two documents that differ only in key order or whitespace get the same text, and so the same content hash.
/// </summary>
public static class SoulCanonicalJson
{
    /// <summary>Returns the canonical text. Throws <see cref="ArgumentException"/> for invalid JSON or duplicate property names.</summary>
    public static string Canonicalize(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        try
        {
            using var doc = JsonDocument.Parse(json);
            using var buffer = new MemoryStream();
            using (var writer = new Utf8JsonWriter(buffer))
            {
                Write(writer, doc.RootElement);
            }
            return Encoding.UTF8.GetString(buffer.ToArray());
        }
        catch (JsonException ex)
        {
            throw new ArgumentException($"Preferences are not valid JSON: {ex.Message}", nameof(json), ex);
        }
    }

    private static void Write(Utf8JsonWriter writer, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                var properties = element.EnumerateObject().ToList();
                if (properties.Select(p => p.Name).Distinct(StringComparer.Ordinal).Count() != properties.Count)
                    throw new ArgumentException("Preferences contain a duplicate property name.");
                writer.WriteStartObject();
                foreach (var property in properties.OrderBy(p => p.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    Write(writer, property.Value);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                    Write(writer, item);
                writer.WriteEndArray();
                break;
            default:
                element.WriteTo(writer);
                break;
        }
    }
}
