using System.Text.Json;
using System.Text.Json.Serialization;

namespace Bazaar.Client.LocalInference.Models;

/// <summary>Source-generated JSON metadata for the manifest. Unknown properties are rejected.</summary>
[JsonSourceGenerationOptions(
    JsonSerializerDefaults.Web,
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(ModelManifest))]
internal sealed partial class ModelManifestJsonContext : JsonSerializerContext
{
}
