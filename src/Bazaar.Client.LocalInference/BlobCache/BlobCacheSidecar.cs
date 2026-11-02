using System.Text.Json;
using System.Text.Json.Serialization;

namespace Bazaar.Client.LocalInference.BlobCache;

/// <summary>The full key recorded beside a cached blob. A read compares it field by field with the requested key.</summary>
internal sealed record BlobCacheSidecar(string ModelSha256, string ExecutionProvider, string? DriverVersion, string RuntimeVersion);

/// <summary>Source-generated JSON metadata for the blob sidecar. Unknown properties are rejected.</summary>
[JsonSourceGenerationOptions(
    JsonSerializerDefaults.Web,
    WriteIndented = true,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(BlobCacheSidecar))]
internal sealed partial class BlobCacheJsonContext : JsonSerializerContext
{
}
