using System.Text.Json;

namespace Bazaar.Kodo.Manifest;

/// <summary>What decoding found: a manifest, or a stable cause from <see cref="ModuleManifestCauses"/>.</summary>
public sealed record ModuleManifestDecoding(ModuleManifest? Manifest, string? Cause);

/// <summary>
/// The strict decoder and the canonical bytes of a module manifest. Decoding caps the size and the nesting depth and rejects
/// duplicate and unknown fields, so the parsed value is exactly what the signature covers. No file IO: callers pass bytes.
/// This is a Bazaar profile of canonical JSON (fixed property order, no whitespace), as for the runtime manifest.
/// </summary>
public static class ModuleManifestCodec
{
    /// <summary>The largest manifest accepted, in bytes.</summary>
    public const int MaxBytes = 64 * 1024;

    /// <summary>The deepest nesting accepted. The real shape needs 3.</summary>
    public const int MaxDepth = 6;

    /// <summary>Decodes manifest bytes. Never throws for bad input.</summary>
    public static ModuleManifestDecoding Decode(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length > MaxBytes)
        {
            return new ModuleManifestDecoding(null, ModuleManifestCauses.TooLarge);
        }

        string? scanProblem = Scan(bytes);
        if (scanProblem is not null)
        {
            return new ModuleManifestDecoding(null, scanProblem);
        }

        try
        {
            ModuleManifest? manifest = JsonSerializer.Deserialize(bytes, ModuleManifestJsonContext.Default.ModuleManifest);
            return manifest is null
                ? new ModuleManifestDecoding(null, ModuleManifestCauses.Malformed)
                : new ModuleManifestDecoding(manifest, null);
        }
        catch (JsonException)
        {
            return new ModuleManifestDecoding(null, ModuleManifestCauses.Malformed);
        }
    }

    /// <summary>The canonical UTF-8 JSON of a manifest without its signature block, which is the message the release key signs.</summary>
    public static byte[] CanonicalBytes(ModuleManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        return JsonSerializer.SerializeToUtf8Bytes(manifest with { Signature = null }, ModuleManifestJsonContext.Default.ModuleManifest);
    }

    /// <summary>Writes a manifest as stored in the package, signature block included when present.</summary>
    public static byte[] Encode(ModuleManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        return JsonSerializer.SerializeToUtf8Bytes(manifest, ModuleManifestJsonContext.Default.ModuleManifest);
    }

    // One pass over the tokens: checks depth and duplicate names per object. Returns a cause, or null when the shape is acceptable.
    private static string? Scan(ReadOnlySpan<byte> bytes)
    {
        Utf8JsonReader reader = new(bytes, new JsonReaderOptions { MaxDepth = 64 });
        Stack<HashSet<string>?> scopes = new();
        try
        {
            while (reader.Read())
            {
                switch (reader.TokenType)
                {
                    case JsonTokenType.StartObject:
                    case JsonTokenType.StartArray:
                        if (scopes.Count >= MaxDepth)
                        {
                            return ModuleManifestCauses.TooDeep;
                        }

                        scopes.Push(reader.TokenType == JsonTokenType.StartObject ? new HashSet<string>(StringComparer.Ordinal) : null);
                        break;
                    case JsonTokenType.EndObject:
                    case JsonTokenType.EndArray:
                        scopes.Pop();
                        break;
                    case JsonTokenType.PropertyName:
                        if (!scopes.Peek()!.Add(reader.GetString()!))
                        {
                            return ModuleManifestCauses.DuplicateField;
                        }

                        break;
                    default:
                        break;
                }
            }
        }
        catch (JsonException)
        {
            return ModuleManifestCauses.Malformed;
        }

        return null;
    }
}
