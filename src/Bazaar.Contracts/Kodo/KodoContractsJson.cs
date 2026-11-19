using System.Text.Json.Serialization;

namespace Bazaar.Contracts.Kodo;

/// <summary>
/// Source-generated JSON metadata for the Kodo status contracts. Options match the other contract contexts: camelCase,
/// nulls omitted, unknown members skipped, so a newer server can add fields without breaking older clients.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip)]
[JsonSerializable(typeof(KodoStatusDto))]
[JsonSerializable(typeof(KodoControlDto))]
public sealed partial class KodoContractsJsonContext : JsonSerializerContext;
