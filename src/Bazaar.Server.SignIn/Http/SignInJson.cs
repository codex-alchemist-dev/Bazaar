using System.Text.Json.Serialization;

namespace Bazaar.Server.SignIn.Http;

internal sealed record ChallengeResponse(string Id, string Challenge, int TimeoutMs, string? RpId);

internal sealed record AssertRequest(string? ChallengeId, string? Assertion, string? Audience);

internal sealed record RedirectResponse(string Redirect);

internal sealed record ErrorResponse(string Error);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(ChallengeResponse))]
[JsonSerializable(typeof(AssertRequest))]
[JsonSerializable(typeof(RedirectResponse))]
[JsonSerializable(typeof(ErrorResponse))]
internal sealed partial class SignInJson : JsonSerializerContext;
