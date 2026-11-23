using System.Text.Json.Serialization;

namespace Bazaar.Client.Athanor.Bridge;

/// <summary>
/// Source-generated JSON metadata for the bridge (no reflection). Unknown properties are rejected, so a message carries
/// only the fields its type declares.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    NumberHandling = JsonNumberHandling.Strict,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(HostMessage))]
[JsonSerializable(typeof(EditorMessage))]
internal sealed partial class BridgeJsonContext : JsonSerializerContext
{
}
