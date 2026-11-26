using System.Text.Json.Serialization;

namespace Bazaar.Kodo.Manifest;

/// <summary>
/// Source-generated JSON for the module manifest. Properties are written in declaration order, with no indentation, so one value
/// has one encoding. Unknown members are rejected, so the bytes that parse are exactly the content that was signed.
/// </summary>
[JsonSourceGenerationOptions(
    WriteIndented = false,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(ModuleManifest))]
internal sealed partial class ModuleManifestJsonContext : JsonSerializerContext
{
}
