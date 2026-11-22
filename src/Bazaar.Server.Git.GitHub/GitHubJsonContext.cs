using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Bazaar.Server.Git.GitHub;

/// <summary>Source-generated JSON for request bodies (AOT-safe, no reflection). Responses are read with <see cref="JsonDocument"/>.</summary>
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(JsonNode))]
internal sealed partial class GitHubJsonContext : JsonSerializerContext;
