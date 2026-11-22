using System.Text.Json.Serialization;

namespace Bazaar.Client.SignIn;

/// <summary>Request body for <c>POST /auth/handoff</c> (PROPOSED, doc 45 section 2.6): the single-use code and the device identity.</summary>
internal sealed record HandoffRequest(
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("device_class")] string DeviceClass,
    [property: JsonPropertyName("client_build")] string ClientBuild);

/// <summary>Request body for <c>POST /auth/refresh</c> (doc 32 section 5).</summary>
internal sealed record RefreshRequest(
    [property: JsonPropertyName("refresh_token")] string RefreshToken,
    [property: JsonPropertyName("device_class")] string DeviceClass,
    [property: JsonPropertyName("client_build")] string ClientBuild);

/// <summary>The token pair both routes answer with.</summary>
internal sealed record TokenReply(
    [property: JsonPropertyName("access_token")] string? AccessToken,
    [property: JsonPropertyName("token_type")] string? TokenType,
    [property: JsonPropertyName("expires_in")] int ExpiresIn,
    [property: JsonPropertyName("refresh_token")] string? RefreshToken);

/// <summary>The 200 body of <c>GET /auth/whoami</c> (doc 32 section 5).</summary>
internal sealed record WhoAmIReply(
    [property: JsonPropertyName("human_id")] Guid HumanId,
    [property: JsonPropertyName("display_name")] string? DisplayName,
    [property: JsonPropertyName("role")] string? Role,
    [property: JsonPropertyName("device_id")] Guid DeviceId);

/// <summary>Source-generated JSON, so the client needs no reflection (CLAUDE.md).</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.Unspecified)]
[JsonSerializable(typeof(HandoffRequest))]
[JsonSerializable(typeof(RefreshRequest))]
[JsonSerializable(typeof(TokenReply))]
[JsonSerializable(typeof(WhoAmIReply))]
internal sealed partial class SignInJsonContext : JsonSerializerContext;
