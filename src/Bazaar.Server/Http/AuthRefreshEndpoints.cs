using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Bazaar.Server.Components.Remote;
using Bazaar.Server.Wire.Connection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Bazaar.Server.Http;

/// <summary>
/// The refresh route of doc 32 section 5: <c>POST /auth/refresh</c> exchanges a live refresh secret for a new access token and a
/// new refresh secret through <see cref="ITokenService"/>. The presented secret is retired by the service. A refused secret answers
/// one generic <c>invalid_grant</c>; the reason goes to the server log only. The route carries no credential, like the device routes.
/// </summary>
public static class AuthRefreshEndpoints
{
    private const string _logCategory = "Bazaar.Server.Http.AuthRefresh";

    /// <summary>Maps the refresh route. The token service is looked up per request, so the route answers 503 until one is registered.</summary>
    public static IEndpointRouteBuilder MapBazaarAuthRefreshRoutes(this IEndpointRouteBuilder routes)
    {
        routes.MapPost("/auth/refresh", (Func<HttpContext, Task<IResult>>)RefreshAsync);
        return routes;
    }

    private static async Task<IResult> RefreshAsync(HttpContext http)
    {
        if (TransportRefusal(http) is { } refusal) return refusal;
        var body = await ReadAsync(http, AuthRefreshWireContext.Default.RefreshBody);
        if (body is null
            || string.IsNullOrWhiteSpace(body.RefreshToken)
            || string.IsNullOrWhiteSpace(body.DeviceClass))
            return HttpSurfaceEndpoints.Error(StatusCodes.Status400BadRequest, "malformed_request");

        var tokens = http.RequestServices.GetService<ITokenService>();
        if (tokens is null) return HttpSurfaceEndpoints.Error(StatusCodes.Status503ServiceUnavailable, "unavailable");

        // The device id is not part of this request; the token service matches the device by its class and the secret.
        var device = new DeviceInfo(string.Empty, body.DeviceClass, body.ClientBuild ?? string.Empty);
        try
        {
            var pair = await tokens.RefreshAsync(body.RefreshToken, device, http.RequestAborted);
            var now = http.RequestServices.GetRequiredService<TimeProvider>().GetUtcNow();
            var expiresIn = Math.Max(0, (int)(pair.AccessTokenExpiresAt - now).TotalSeconds);
            return Json(new TokenBodyResponse(pair.AccessToken, "Bearer", expiresIn, pair.RefreshToken), StatusCodes.Status200OK);
        }
        catch (TokenRefreshException refused)
        {
            var logger = http.RequestServices.GetService<ILoggerFactory>()?.CreateLogger(_logCategory);
            if (logger is not null) AuthRefreshLog.RefreshRefused(logger, refused.Failure);
            return HttpSurfaceEndpoints.Error(StatusCodes.Status401Unauthorized, "invalid_grant");
        }
    }

    /// <summary>The same transport rule as the device routes: 403 for plaintext off the machine.</summary>
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
        Results.Json(value, AuthRefreshWireContext.Default.Options, contentType: "application/json", statusCode: status);
}

/// <summary>Request body for <c>POST /auth/refresh</c>.</summary>
public sealed record RefreshBody(
    [property: JsonPropertyName("refresh_token")] string? RefreshToken,
    [property: JsonPropertyName("device_class")] string? DeviceClass,
    [property: JsonPropertyName("client_build")] string? ClientBuild);

/// <summary>Source-generated JSON for the refresh route (CLAUDE.md: System.Text.Json source generation).</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.Unspecified)]
[JsonSerializable(typeof(RefreshBody))]
[JsonSerializable(typeof(TokenBodyResponse))]
internal sealed partial class AuthRefreshWireContext : JsonSerializerContext;

/// <summary>Log lines for the refresh route. The reason is logged; the secret never is.</summary>
internal static partial class AuthRefreshLog
{
    [LoggerMessage(Level = LogLevel.Information, Message = "Refresh credential refused: {Reason}.")]
    public static partial void RefreshRefused(ILogger logger, TokenRefreshFailure reason);
}
