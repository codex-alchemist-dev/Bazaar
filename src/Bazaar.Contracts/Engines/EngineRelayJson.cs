using System.Text.Json.Serialization;

namespace Bazaar.Contracts.Engines;

/// <summary>
/// Source-generated JSON metadata for the relay-request wire payloads. Kept apart from <c>WireJsonContext</c>, which only the
/// wire work package edits. Options match that context: camelCase, nulls omitted, unknown members skipped.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip)]
[JsonSerializable(typeof(EngineRelayRequestEvent))]
[JsonSerializable(typeof(EngineRelayResultRequest))]
public sealed partial class EngineRelayJsonContext : JsonSerializerContext;
