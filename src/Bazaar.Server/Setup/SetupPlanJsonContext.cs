using System.Text.Json;
using System.Text.Json.Serialization;

namespace Bazaar.Server.Setup;

/// <summary>
/// Source-generated JSON for the setup plan and its file form (doc 45 section 1.5). JSON only, through System.Text.Json, as the
/// wire format decision requires. Properties are camelCase and written in declaration order; the canonical form re-sorts keys.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    WriteIndented = false)]
[JsonSerializable(typeof(SetupPlan))]
internal sealed partial class SetupPlanJsonContext : JsonSerializerContext
{
}
