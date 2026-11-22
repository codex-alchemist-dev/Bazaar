using System.Text.Json.Serialization;

namespace Bazaar.Server.Setup;

/// <summary>
/// Source-generated JSON for <c>template.json</c> (doc 45 section 1.8), in the same style as <see cref="SetupPlanJsonContext"/>.
/// Properties are camelCase and written in declaration order; the canonical hash form re-sorts keys.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    WriteIndented = false)]
[JsonSerializable(typeof(SetupTemplate))]
public sealed partial class SetupTemplateJsonContext : JsonSerializerContext
{
}
