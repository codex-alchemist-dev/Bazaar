using System.Text.Json.Serialization;

namespace Bazaar.Server.TalentPool.Component;

/// <summary>Source-generated JSON for the stored manifest.</summary>
[JsonSerializable(typeof(SoulManifest))]
[JsonSerializable(typeof(HirePreferences))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
internal sealed partial class TalentJson : JsonSerializerContext;

/// <summary>Soul version preferences written at hire.</summary>
internal sealed record HirePreferences(string Template, string ContentSha, IReadOnlyList<string> ToolAllowlist);
