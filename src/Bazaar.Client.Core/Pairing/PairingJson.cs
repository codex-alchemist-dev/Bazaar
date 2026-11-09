using System.Text.Json.Serialization;

namespace Bazaar.Client.Core.Pairing;

/// <summary>Start of the device authorization: the code the approver typed or scanned.</summary>
internal sealed record DeviceAuthorizationRequest(
    [property: JsonPropertyName("user_code")] string UserCode,
    [property: JsonPropertyName("label")] string Label,
    [property: JsonPropertyName("device_class")] string DeviceClass);

/// <summary>The server's answer to the start: the polled device code and the poll interval in seconds.</summary>
internal sealed record DeviceAuthorizationResponse(
    [property: JsonPropertyName("device_code")] string DeviceCode,
    [property: JsonPropertyName("interval")] int Interval);

/// <summary>One poll of the token endpoint (RFC 8628 grant type).</summary>
internal sealed record TokenRequest(
    [property: JsonPropertyName("device_code")] string DeviceCode,
    [property: JsonPropertyName("grant_type")] string GrantType);

/// <summary>A successful token answer: the credential pair the device keeps.</summary>
internal sealed record TokenResponse(
    [property: JsonPropertyName("access_token")] string AccessToken,
    [property: JsonPropertyName("refresh_token")] string RefreshToken,
    [property: JsonPropertyName("expires_in")] int ExpiresIn);

/// <summary>An error answer from the token endpoint, such as <c>authorization_pending</c>.</summary>
internal sealed record TokenErrorResponse([property: JsonPropertyName("error")] string Error);

/// <summary>Source-generated JSON for the pairing wire messages (CLAUDE.md: System.Text.Json source generation).</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.Unspecified)]
[JsonSerializable(typeof(DeviceAuthorizationRequest))]
[JsonSerializable(typeof(DeviceAuthorizationResponse))]
[JsonSerializable(typeof(TokenRequest))]
[JsonSerializable(typeof(TokenResponse))]
[JsonSerializable(typeof(TokenErrorResponse))]
internal sealed partial class PairingJsonContext : JsonSerializerContext;
