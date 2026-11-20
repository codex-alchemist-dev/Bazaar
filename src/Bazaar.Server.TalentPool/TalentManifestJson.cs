using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Bazaar.Server.TalentPool;

/// <summary>
/// The one JSON form of a manifest: fixed property order (<c>prompt</c>, <c>tools</c>, <c>models</c>), lists already sorted by the validator.
/// The stored <c>manifest</c> column and the content hash both come from it. <c>TemplateId</c> is identity, not content, so it is not in it.
/// Written by hand with <see cref="Utf8JsonWriter"/> so the bytes are fixed and trimming-safe.
/// </summary>
internal static class TalentManifestJson
{
    public static string Write(TalentManifest manifest)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("prompt", manifest.Prompt);
            writer.WriteStartArray("tools");
            foreach (var tool in manifest.ToolAllowlist)
                writer.WriteStringValue(tool);
            writer.WriteEndArray();
            writer.WriteStartArray("models");
            foreach (var model in manifest.ModelWhitelist)
                writer.WriteStringValue(model);
            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    public static string Sha(TalentManifest manifest) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Write(manifest))));

    /// <summary>Reads the stored form back. Throws <see cref="InvalidDataException"/> when the document is not a canonical manifest.</summary>
    public static TalentManifest Read(string json, Guid templateId)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("prompt", out var prompt) || prompt.ValueKind != JsonValueKind.String
            || !root.TryGetProperty("tools", out var tools) || tools.ValueKind != JsonValueKind.Array
            || !root.TryGetProperty("models", out var models) || models.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("Stored talent manifest is not in the canonical shape.");
        }

        return new TalentManifest(prompt.GetString()!, Strings(tools), Strings(models), templateId);

        static string[] Strings(JsonElement array) =>
            [.. array.EnumerateArray().Select(e => e.ValueKind == JsonValueKind.String
                ? e.GetString()!
                : throw new InvalidDataException("Stored talent manifest list holds a non-string."))];
    }
}
