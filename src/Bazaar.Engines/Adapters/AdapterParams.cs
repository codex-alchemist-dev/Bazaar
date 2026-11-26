using System.Text.Json.Serialization;
using Bazaar.Contracts.Engines;

namespace Bazaar.Engines.Adapters;

/// <summary>Params for <c>open</c> and <c>close</c>.</summary>
internal sealed record AdapterProjectParams([property: JsonPropertyName("project_id")] string ProjectId);

/// <summary>
/// Params for <c>command</c>: the <see cref="EngineProjectEnvelope"/> fields plus the <see cref="EngineCommand"/> body.
/// <c>seq</c> is the last adapter stream sequence the host has seen for the project, so the adapter can tell how far
/// the server has read.
/// </summary>
internal sealed record AdapterCommandParams(
    [property: JsonPropertyName("project_id")] string ProjectId,
    [property: JsonPropertyName("command_id")] string CommandId,
    [property: JsonPropertyName("seq")] long Seq,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("arguments")] System.Text.Json.JsonElement? Arguments);
