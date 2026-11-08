using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Text.Json.Serialization;
using Bazaar.Server.Components.Remote;
using Bazaar.Server.Providers;
using Bazaar.Server.Wire.Connection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Bazaar.Server.Http;

/// <summary>
/// The pairing routes of doc 32 section 3 (PROPOSED). The approver's routes are authenticated like every other route: <c>POST /pair/offers</c>
/// creates an offer for the signed-in human, and <c>POST /pair/offers/{id}/approve|deny</c> answers it. The device's two routes,
/// <c>POST /pair/device-authorization</c> and <c>POST /pair/token</c>, have no credential yet, so they are reachable without one. Their
/// answers follow RFC 8628 error codes. Plaintext is allowed only on loopback or an allowed overlay, as for every route.
/// </summary>
public static class PairingEndpoints
{
    /// <summary>The RFC 8628 grant type the device sends on its token poll.</summary>
    public const string DeviceCodeGrantType = "urn:ietf:params:oauth:grant-type:device_code";

    /// <summary>Maps the pairing routes. The pairing service must be registered (<c>AddBazaarPairing</c>).</summary>
    public static IEndpointRouteBuilder MapBazaarPairingRoutes(this IEndpointRouteBuilder routes)
    {
        routes.MapPost("/pair/offers", (Func<HttpContext, Task<IResult>>)(http =>
            HttpSurfaceEndpoints.Guarded(http, CreateOfferAsync)));
        routes.MapPost("/pair/offers/{id}/approve", (HttpContext http, Guid id) =>
            HttpSurfaceEndpoints.Guarded(http, (h, p) => AnswerOfferAsync(h, p, id, approve: true)));
        routes.MapPost("/pair/offers/{id}/deny", (HttpContext http, Guid id) =>
            HttpSurfaceEndpoints.Guarded(http, (h, p) => AnswerOfferAsync(h, p, id, approve: false)));
        routes.MapPost("/pair/device-authorization", (Func<HttpContext, Task<IResult>>)DeviceAuthorizationAsync);
        routes.MapPost("/pair/token", (Func<HttpContext, Task<IResult>>)TokenAsync);
        return routes;
    }

    private static async Task<IResult> CreateOfferAsync(HttpContext http, PrincipalRef principal)
    {
        var body = await ReadAsync(http, PairingWireContext.Default.CreateOfferBody);
        if (body is null) return HttpSurfaceEndpoints.Error(StatusCodes.Status400BadRequest, "malformed_request");
        try
        {
            var created = await http.RequestServices.GetRequiredService<IPairingService>()
                .CreateOfferAsync(principal.Id, body.Role ?? string.Empty, http.RequestAborted);
            return Json(new OfferCreatedBody(created.OfferId, created.UserCode, created.ExpiresAt), StatusCodes.Status201Created);
        }
        catch (PairingRefusedException refused)
        {
            return HttpSurfaceEndpoints.Error(StatusCodes.Status400BadRequest, refused.Code);
        }
    }

    private static async Task<IResult> AnswerOfferAsync(HttpContext http, PrincipalRef principal, Guid offerId, bool approve)
    {
        var pairing = http.RequestServices.GetRequiredService<IPairingService>();
        try
        {
            if (approve) await pairing.ApproveAsync(offerId, principal.Id, http.RequestAborted);
            else await pairing.DenyAsync(offerId, principal.Id, http.RequestAborted);
            return Results.NoContent();
        }
        catch (PairingRefusedException refused)
        {
            return HttpSurfaceEndpoints.Error(StatusCodes.Status400BadRequest, refused.Code);
        }
    }

    private static async Task<IResult> DeviceAuthorizationAsync(HttpContext http)
    {
        if (TransportRefusal(http) is { } refusal) return refusal;
        var body = await ReadAsync(http, PairingWireContext.Default.DeviceAuthorizationBody);
        if (body is null) return HttpSurfaceEndpoints.Error(StatusCodes.Status400BadRequest, "malformed_request");
        try
        {
            var auth = await http.RequestServices.GetRequiredService<IPairingService>()
                .StartDeviceAuthorizationAsync(body.UserCode ?? string.Empty, body.Label ?? string.Empty, body.DeviceClass ?? string.Empty, http.RequestAborted);
            return Json(new DeviceAuthorizationBodyResponse(auth.DeviceCode, auth.IntervalSeconds), StatusCodes.Status200OK);
        }
        catch (PairingRefusedException refused)
        {
            return HttpSurfaceEndpoints.Error(StatusCodes.Status400BadRequest, refused.Code);
        }
    }

    private static async Task<IResult> TokenAsync(HttpContext http)
    {
        if (TransportRefusal(http) is { } refusal) return refusal;
        var body = await ReadAsync(http, PairingWireContext.Default.TokenBody);
        if (body is null) return HttpSurfaceEndpoints.Error(StatusCodes.Status400BadRequest, "malformed_request");
        if (body.GrantType != DeviceCodeGrantType)
            return HttpSurfaceEndpoints.Error(StatusCodes.Status400BadRequest, "unsupported_grant_type");

        try
        {
            var poll = await http.RequestServices.GetRequiredService<IPairingService>()
                .PollAsync(body.DeviceCode ?? string.Empty, http.RequestAborted);
            return poll.Status switch
            {
                PairingPollStatus.Pending => HttpSurfaceEndpoints.Error(StatusCodes.Status400BadRequest, "authorization_pending"),
                PairingPollStatus.SlowDown => HttpSurfaceEndpoints.Error(StatusCodes.Status400BadRequest, "slow_down"),
                _ => GrantedResponse(http, poll.Tokens!),
            };
        }
        catch (PairingPollException ended)
        {
            return HttpSurfaceEndpoints.Error(StatusCodes.Status400BadRequest, ended.Error);
        }
    }

    private static IResult GrantedResponse(HttpContext http, TokenPair tokens)
    {
        var now = http.RequestServices.GetRequiredService<TimeProvider>().GetUtcNow();
        var expiresIn = Math.Max(0, (int)(tokens.AccessTokenExpiresAt - now).TotalSeconds);
        return Json(new TokenBodyResponse(tokens.AccessToken, "Bearer", expiresIn, tokens.RefreshToken), StatusCodes.Status200OK);
    }

    /// <summary>The device routes apply the transport rule that the authenticated routes apply: 403 for plaintext off the machine.</summary>
    private static IResult? TransportRefusal(HttpContext http)
    {
        var wire = http.RequestServices.GetRequiredService<IOptions<WireOptions>>().Value;
        var remote = http.Connection.RemoteIpAddress;
        var plaintextRemote = !http.Request.IsHttps && !wire.AllowPlaintextOverlay && remote is not null && !System.Net.IPAddress.IsLoopback(remote);
        return plaintextRemote ? HttpSurfaceEndpoints.Error(StatusCodes.Status403Forbidden, "forbidden") : null;
    }

    private static async Task<T?> ReadAsync<T>(HttpContext http, JsonTypeInfo<T> typeInfo) where T : class
    {
        try
        {
            return await http.Request.ReadFromJsonAsync(typeInfo, http.RequestAborted);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static IResult Json<T>(T value, int status) =>
        Results.Json(value, PairingWireContext.Default.Options, contentType: "application/json", statusCode: status);
}

/// <summary>Request bodies for the pairing routes.</summary>
public sealed record CreateOfferBody([property: JsonPropertyName("role")] string? Role);

public sealed record DeviceAuthorizationBody(
    [property: JsonPropertyName("user_code")] string? UserCode,
    [property: JsonPropertyName("label")] string? Label,
    [property: JsonPropertyName("device_class")] string? DeviceClass);

public sealed record TokenBody(
    [property: JsonPropertyName("device_code")] string? DeviceCode,
    [property: JsonPropertyName("grant_type")] string? GrantType);

/// <summary>Response bodies for the pairing routes. Token and device secrets appear here once, then only their hashes stay on the server.</summary>
public sealed record OfferCreatedBody(
    [property: JsonPropertyName("offer_id")] Guid OfferId,
    [property: JsonPropertyName("user_code")] string UserCode,
    [property: JsonPropertyName("expires_at")] DateTimeOffset ExpiresAt);

public sealed record DeviceAuthorizationBodyResponse(
    [property: JsonPropertyName("device_code")] string DeviceCode,
    [property: JsonPropertyName("interval")] int Interval);

public sealed record TokenBodyResponse(
    [property: JsonPropertyName("access_token")] string AccessToken,
    [property: JsonPropertyName("token_type")] string TokenType,
    [property: JsonPropertyName("expires_in")] int ExpiresIn,
    [property: JsonPropertyName("refresh_token")] string RefreshToken);

/// <summary>Source-generated JSON for the pairing routes (CLAUDE.md: System.Text.Json source generation).</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.Unspecified)]
[JsonSerializable(typeof(CreateOfferBody))]
[JsonSerializable(typeof(DeviceAuthorizationBody))]
[JsonSerializable(typeof(TokenBody))]
[JsonSerializable(typeof(OfferCreatedBody))]
[JsonSerializable(typeof(DeviceAuthorizationBodyResponse))]
[JsonSerializable(typeof(TokenBodyResponse))]
internal sealed partial class PairingWireContext : JsonSerializerContext;
