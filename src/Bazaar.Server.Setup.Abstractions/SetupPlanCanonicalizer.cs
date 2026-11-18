using System.Buffers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Bazaar.Server.Setup;

/// <summary>
/// The canonical form of a setup plan and its hash (doc 45 section 1.2). Canonical JSON means UTF-8, object keys sorted by
/// ordinal comparison at every level, no insignificant whitespace, arrays in their stored order. The plan hash is the lowercase
/// hex SHA-256 of those bytes. Two plans that differ only in property order, or in whitespace, hash the same.
/// </summary>
public static class SetupPlanCanonicalizer
{
    /// <summary>The plan as a JSON node, written by the source-generated context.</summary>
    public static JsonNode? ToNode(SetupPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return JsonSerializer.SerializeToNode(plan, SetupPlanJsonContext.Default.SetupPlan);
    }

    /// <summary>The canonical UTF-8 bytes of a plan. The plan is serialized with the source-generated context first.</summary>
    public static byte[] ToCanonicalUtf8(SetupPlan plan) => CanonicalUtf8(ToNode(plan));

    /// <summary>The canonical UTF-8 bytes of any JSON node.</summary>
    public static byte[] CanonicalUtf8(JsonNode? node)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = false }))
        {
            WriteCanonical(writer, node);
        }

        return buffer.WrittenSpan.ToArray();
    }

    /// <summary>The plan hash: lowercase hex SHA-256 over the canonical bytes.</summary>
    public static string HashOf(byte[] canonicalUtf8)
    {
        ArgumentNullException.ThrowIfNull(canonicalUtf8);
        return Convert.ToHexStringLower(SHA256.HashData(canonicalUtf8));
    }

    /// <summary>The plan hash of a plan.</summary>
    public static string Hash(SetupPlan plan) => HashOf(ToCanonicalUtf8(plan));

    /// <summary>
    /// Flattens a node to leaf paths (dotted object keys) with the canonical text of each leaf. Objects are walked; scalars,
    /// nulls, empty objects and every array are leaves, because a list is one value (for example the module list).
    /// </summary>
    public static IReadOnlyDictionary<string, string> Leaves(JsonNode? node)
    {
        var leaves = new Dictionary<string, string>(StringComparer.Ordinal);
        Flatten(node, string.Empty, leaves);
        return leaves;
    }

    private static void Flatten(JsonNode? node, string path, Dictionary<string, string> leaves)
    {
        if (node is JsonObject obj && obj.Count > 0)
        {
            foreach (var property in obj.OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                var childPath = path.Length == 0 ? property.Key : path + "." + property.Key;
                Flatten(property.Value, childPath, leaves);
            }

            return;
        }

        leaves[path] = Text(node);
    }

    /// <summary>The canonical text of one node, as it appears inside the canonical form.</summary>
    public static string Text(JsonNode? node) => System.Text.Encoding.UTF8.GetString(CanonicalUtf8(node));

    private static void WriteCanonical(Utf8JsonWriter writer, JsonNode? node)
    {
        switch (node)
        {
            case null:
                writer.WriteNullValue();
                break;
            case JsonObject obj:
                writer.WriteStartObject();
                foreach (var property in obj.OrderBy(p => p.Key, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Key);
                    WriteCanonical(writer, property.Value);
                }

                writer.WriteEndObject();
                break;
            case JsonArray array:
                writer.WriteStartArray();
                foreach (var item in array)
                {
                    WriteCanonical(writer, item);
                }

                writer.WriteEndArray();
                break;
            default:
                node.WriteTo(writer);
                break;
        }
    }
}
