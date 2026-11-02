using System.Text.Json.Serialization;

namespace Bazaar.Contracts.Engines;

/// <summary>
/// The fields every adapter message carries (docs/design/20-engine-integration-api.md, Data and interfaces).
/// Wire names follow the design doc (<c>project_id</c>, <c>command_id</c>, <c>seq</c>), not the camelCase default.
/// <c>Seq</c> numbers the adapter's own stream per project. GC sequence numbers are a different counter and are not used here.
/// </summary>
public sealed record EngineProjectEnvelope(
    [property: JsonPropertyName("project_id")] string ProjectId,
    [property: JsonPropertyName("command_id")] string CommandId,
    [property: JsonPropertyName("seq")] long Seq);
