using System.Text.Json;
using System.Text.Json.Serialization;

namespace Bazaar.Kodo.Anchoring;

/// <summary>Source-generated JSON for anchors: camelCase, compact, unknown members rejected.</summary>
[JsonSourceGenerationOptions(
    WriteIndented = false,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(AnchorRecord))]
internal sealed partial class AnchorJsonContext : JsonSerializerContext
{
}
