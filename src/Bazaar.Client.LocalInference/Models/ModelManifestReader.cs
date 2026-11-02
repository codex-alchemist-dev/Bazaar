using System.Text.Json;
using System.Text.RegularExpressions;

namespace Bazaar.Client.LocalInference.Models;

/// <summary>
/// Reads and writes model manifests (doc 31, Model packaging and delivery). Parsing validates every field, because
/// model ids, versions and file names become path components in the local store.
/// </summary>
public static partial class ModelManifestReader
{
    /// <summary>File name of the manifest inside a model version folder.</summary>
    public const string ManifestFileName = "manifest.json";

    /// <summary>Parses and validates a manifest. Throws <see cref="ModelFormatException"/> when it is not usable.</summary>
    public static ModelManifest Parse(string json)
    {
        ModelManifest? manifest;
        try
        {
            manifest = JsonSerializer.Deserialize(json, ModelManifestJsonContext.Default.ModelManifest);
        }
        catch (JsonException error)
        {
            throw new ModelFormatException($"Manifest is not valid JSON for the manifest schema: {error.Message}", error);
        }

        if (manifest is null)
        {
            throw new ModelFormatException("Manifest is empty.");
        }

        Validate(manifest);
        return manifest;
    }

    /// <summary>Writes a validated manifest as JSON.</summary>
    public static string Serialize(ModelManifest manifest)
    {
        Validate(manifest);
        return JsonSerializer.Serialize(manifest, ModelManifestJsonContext.Default.ModelManifest);
    }

    /// <summary>Checks every manifest rule. Throws <see cref="ModelFormatException"/> on the first breach.</summary>
    public static void Validate(ModelManifest manifest)
    {
        if (!IsSafeName(manifest.Model))
        {
            throw new ModelFormatException($"Model id '{manifest.Model}' must be a plain identifier (letters, digits, '.', '_', '-').");
        }

        if (!IsSafeName(manifest.Version))
        {
            throw new ModelFormatException($"Version '{manifest.Version}' must be a plain identifier (letters, digits, '.', '_', '-').");
        }

        if (string.IsNullOrWhiteSpace(manifest.License))
        {
            throw new ModelFormatException("Manifest license is required.");
        }

        if (string.IsNullOrWhiteSpace(manifest.MinRuntime))
        {
            throw new ModelFormatException("Manifest minRuntime is required.");
        }

        if (manifest.Files is null || manifest.Files.Count == 0)
        {
            throw new ModelFormatException("Manifest must list at least one file.");
        }

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in manifest.Files)
        {
            if (file is null)
            {
                throw new ModelFormatException("Manifest contains an empty file entry.");
            }

            if (!IsSafeName(file.Name) || string.Equals(file.Name, ManifestFileName, StringComparison.OrdinalIgnoreCase))
            {
                throw new ModelFormatException($"File name '{file.Name}' must be a plain file name and cannot be '{ManifestFileName}'.");
            }

            if (!names.Add(file.Name))
            {
                throw new ModelFormatException($"File '{file.Name}' is listed more than once.");
            }

            if (file.Sha256 is null || !Sha256Regex().IsMatch(file.Sha256))
            {
                throw new ModelFormatException($"File '{file.Name}' needs a 64-character hexadecimal SHA-256.");
            }

            if (file.Bytes < 0)
            {
                throw new ModelFormatException($"File '{file.Name}' has a negative byte length.");
            }
        }

        if (manifest.Signature is not null && string.IsNullOrWhiteSpace(manifest.Signature))
        {
            throw new ModelFormatException("Manifest signature is empty. Omit the field when unsigned.");
        }
    }

    /// <summary>True when the name is safe to use as one path component.</summary>
    public static bool IsSafeName(string? name) => name is not null && SafeNameRegex().IsMatch(name);

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$")]
    private static partial Regex SafeNameRegex();

    [GeneratedRegex("^[0-9a-fA-F]{64}$")]
    private static partial Regex Sha256Regex();
}
