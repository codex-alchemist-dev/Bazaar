using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Bazaar.Server.Gateway.Catalogue;

/// <summary>The default catalogue file is malformed, repeats an id, names an unknown adapter, or looks like it holds a secret.</summary>
public sealed class DefaultCatalogueException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Source-generated JSON for the default catalogue file (AOT-safe).</summary>
[JsonSerializable(typeof(DefaultCatalogue))]
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    NumberHandling = JsonNumberHandling.Strict,
    UseStringEnumConverter = true)]
internal sealed partial class DefaultCatalogueJson : JsonSerializerContext;

/// <summary>
/// Reads the shipped default catalogue (docs/design/05-model-gateway.md, "Default catalogue"). The file must stay secret-free,
/// so the loader refuses any property whose name contains key, secret, token or password and any value starting with <c>sk-</c>.
/// </summary>
public static class DefaultCatalogueLoader
{
    /// <summary>Logical name of the embedded resource.</summary>
    public const string ResourceName = "Bazaar.Server.Gateway.Catalogue.DefaultCatalogue.default-catalogue.json";

    private static readonly string[] _secretNameParts = ["key", "secret", "token", "password"];

    /// <summary>Loads the file shipped inside this assembly.</summary>
    public static DefaultCatalogue Load()
    {
        using var stream = typeof(DefaultCatalogueLoader).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new DefaultCatalogueException($"Embedded resource '{ResourceName}' is missing.");
        return Load(stream);
    }

    /// <summary>Loads and validates a catalogue from a stream.</summary>
    public static DefaultCatalogue Load(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        try
        {
            using var doc = JsonDocument.Parse(stream);
            RejectSecrets(doc.RootElement, "$");
            var catalogue = doc.RootElement.Deserialize(DefaultCatalogueJson.Default.DefaultCatalogue)
                ?? throw new DefaultCatalogueException("The catalogue file is empty.");
            Validate(catalogue);
            return catalogue;
        }
        catch (JsonException ex)
        {
            throw new DefaultCatalogueException($"The catalogue file is not valid: {ex.Message}", ex);
        }
    }

    private static void Validate(DefaultCatalogue catalogue)
    {
        if (catalogue.Entries is null)
        {
            throw new DefaultCatalogueException("The catalogue has no entries list.");
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in catalogue.Entries)
        {
            if (entry is null || string.IsNullOrWhiteSpace(entry.Id))
            {
                throw new DefaultCatalogueException("Every entry needs an id.");
            }

            if (!seen.Add(entry.Id))
            {
                throw new DefaultCatalogueException($"Duplicate entry id '{entry.Id}'.");
            }

            if (entry.BaseUrl is not null
                && !(Uri.TryCreate(entry.BaseUrl, UriKind.Absolute, out var uri)
                    && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)))
            {
                throw new DefaultCatalogueException($"Entry '{entry.Id}' has a baseUrl that is not an absolute http(s) URL.");
            }
        }
    }

    private static void RejectSecrets(JsonElement element, string path)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    foreach (var part in _secretNameParts)
                    {
                        if (property.Name.Contains(part, StringComparison.OrdinalIgnoreCase))
                        {
                            throw new DefaultCatalogueException($"Property '{path}.{property.Name}' looks like a secret.");
                        }
                    }

                    RejectSecrets(property.Value, $"{path}.{property.Name}");
                }

                break;
            case JsonValueKind.Array:
                var index = 0;
                foreach (var item in element.EnumerateArray())
                {
                    RejectSecrets(item, $"{path}[{index++}]");
                }

                break;
            case JsonValueKind.String:
                if (element.GetString()!.TrimStart().StartsWith("sk-", StringComparison.OrdinalIgnoreCase))
                {
                    throw new DefaultCatalogueException($"The value at '{path}' looks like a secret.");
                }

                break;
        }
    }
}
