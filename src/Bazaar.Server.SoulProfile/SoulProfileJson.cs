using System.Text.Json.Serialization;

namespace Bazaar.Server.SoulProfile;

internal sealed record VersionBody(
    [property: JsonPropertyName("version")] int Version,
    [property: JsonPropertyName("parent_version")] int? ParentVersion,
    [property: JsonPropertyName("charter_sha256")] string CharterSha256,
    [property: JsonPropertyName("preferences_json")] string PreferencesJson,
    [property: JsonPropertyName("content_sha256")] string ContentSha256,
    [property: JsonPropertyName("author")] string Author,
    [property: JsonPropertyName("reason")] string Reason,
    [property: JsonPropertyName("decision_ref")] string? DecisionRef,
    [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt);

internal sealed record ProfileBody(
    [property: JsonPropertyName("soul_id")] Guid SoulId,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("model_badge")] string ModelBadge,
    [property: JsonPropertyName("versions")] VersionBody[] Versions);

internal sealed record ErrorBody([property: JsonPropertyName("error")] string Error);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.Unspecified)]
[JsonSerializable(typeof(ProfileBody))]
[JsonSerializable(typeof(ErrorBody))]
internal sealed partial class SoulProfileJson : JsonSerializerContext;
