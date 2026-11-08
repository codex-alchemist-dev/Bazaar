using System.Text.Json;
using System.Text.Json.Serialization;

namespace Bazaar.Server.Http;

/// <summary>
/// The JSON body of <c>POST /constellation/tiles</c>: one viewport request. <c>tier</c> is <c>Far</c>, <c>Mid</c> or <c>Near</c>.
/// The node cap is applied by the server, so a larger <c>max_nodes</c> is clamped, not refused.
/// </summary>
public sealed record ConstellationTileRequestBody(
    [property: JsonPropertyName("view_id")] string? ViewId,
    [property: JsonPropertyName("graph_version")] long GraphVersion,
    [property: JsonPropertyName("tier")] string? Tier,
    [property: JsonPropertyName("min_x")] float MinX,
    [property: JsonPropertyName("min_y")] float MinY,
    [property: JsonPropertyName("min_z")] float MinZ,
    [property: JsonPropertyName("max_x")] float MaxX,
    [property: JsonPropertyName("max_y")] float MaxY,
    [property: JsonPropertyName("max_z")] float MaxZ,
    [property: JsonPropertyName("max_nodes")] int MaxNodes);

/// <summary>Source-generated JSON for the tile request body (no reflection at runtime, per the wire rules).</summary>
[JsonSerializable(typeof(ConstellationTileRequestBody))]
[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
public sealed partial class ConstellationTileJsonContext : JsonSerializerContext
{
}
