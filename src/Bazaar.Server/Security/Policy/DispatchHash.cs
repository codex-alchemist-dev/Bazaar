using System.Buffers;
using System.Security.Cryptography;
using System.Text.Json;
using Bazaar.Server.Security.Taint;

namespace Bazaar.Server.Security.Policy;

/// <summary>
/// The hash an owner approval is bound to (doc 15, "Approval flow"): SHA-256 over the canonical form of the tool, its
/// arguments, its target and the taint sources. Changing any of them voids the approval.
/// </summary>
/// <remarks>
/// Canonical form: object keys sorted ordinally, no whitespace, strings in the writer's default escaping, numbers as
/// their source text. An arguments object with a repeated key is refused, because two readers could otherwise see
/// different values for one hash.
/// </remarks>
public static class DispatchHash
{
    /// <summary>Length in bytes of every dispatch hash.</summary>
    public const int Length = 32;

    /// <summary>Computes the dispatch hash for a call.</summary>
    /// <exception cref="ArgumentException">The arguments contain a repeated property name.</exception>
    public static byte[] Compute(string tool, string? target, JsonElement args, IEnumerable<TaintSource> taintSources)
    {
        ArgumentNullException.ThrowIfNull(tool);
        ArgumentNullException.ThrowIfNull(taintSources);

        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WritePropertyName("args");
            WriteCanonical(writer, args);
            writer.WriteString("target", target);
            writer.WriteStartArray("taint");
            foreach (var source in taintSources)
            {
                writer.WriteStartObject();
                writer.WriteString("id", source.SourceId);
                writer.WriteString("raised", source.Raised.ToString());
                writer.WriteString("trust", source.Trust.ToString());
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteString("tool", tool);
            writer.WriteEndObject();
        }

        return SHA256.HashData(buffer.WrittenSpan);
    }

    /// <summary>Hash of a dispatch context, using its own tool, target, arguments and taint.</summary>
    public static byte[] Compute(DispatchContext context) =>
        Compute(context.Tool, context.Target, context.Args, context.Taint.Sources);

    private static void WriteCanonical(Utf8JsonWriter writer, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                var properties = element.EnumerateObject().Select(p => (p.Name, p.Value)).ToList();
                properties.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
                for (var i = 0; i < properties.Count; i++)
                {
                    if (i > 0 && properties[i - 1].Name == properties[i].Name)
                    {
                        throw new ArgumentException($"Arguments repeat the property '{properties[i].Name}'.", nameof(element));
                    }

                    writer.WritePropertyName(properties[i].Name);
                    WriteCanonical(writer, properties[i].Value);
                }

                writer.WriteEndObject();
                break;

            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                {
                    WriteCanonical(writer, item);
                }

                writer.WriteEndArray();
                break;

            case JsonValueKind.String:
                writer.WriteStringValue(element.GetString());
                break;

            case JsonValueKind.Number:
                writer.WriteRawValue(element.GetRawText());
                break;

            case JsonValueKind.True:
                writer.WriteBooleanValue(true);
                break;

            case JsonValueKind.False:
                writer.WriteBooleanValue(false);
                break;

            case JsonValueKind.Null:
                writer.WriteNullValue();
                break;

            default:
                throw new ArgumentException($"Unsupported JSON value kind {element.ValueKind}.", nameof(element));
        }
    }
}
