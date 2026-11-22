using System.Text.Json.Serialization;
using Bazaar.Contracts.Soul;

namespace Bazaar.Server.Souls.Import;

/// <summary>
/// Strict source-generated JSON for <c>manifest.json</c>: camelCase names, and any member the manifest type does not
/// declare is an error. Parsing never falls back to reflection.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(BundleManifest))]
public sealed partial class BundleManifestJsonContext : JsonSerializerContext
{
}
