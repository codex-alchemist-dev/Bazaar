using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using Bazaar.Server.Components.Remote;
using Bazaar.Server.Wire.Connection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Bazaar.Server.SignIn;

/// <summary>
/// The native redeem route of doc 32 section 4: <c>POST /auth/handoff</c> trades a one-time handoff code (audience <c>native</c>) for the
/// device's first token pair, in the same body as <c>POST /auth/refresh</c>. Every refusal of the code (unknown, used, expired, wrong
/// audience, malformed) answers the same 401 <c>invalid_grant</c>; the reason goes to the server log only and the code never does.
/// The rate-limit bucket is <see cref="SignInRateLimitBuckets.SignIn"/>; enforcement belongs to the rate-limit module.
/// </summary>
public static class AuthHandoffEndpoints
{
    private const string _logCategory = "Bazaar.Server.SignIn.AuthHandoff";
    private const int _maxBodyBytes = 4 * 1024;
    private const int _maxCodeLength = 512;

    /// <summary>Maps <c>POST /auth/handoff</c>. Served only while <c>signin:enabled</c> (the native audience) is on.</summary>
    public static IEndpointRouteBuilder MapBazaarAuthHandoff(this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);
        routes.Map("/auth/handoff", HandleAsync).WithDisplayName("bazaar-auth-handoff");
        return routes;
    }

    // A RequestDelegate keeps the route trim and AOT safe (no delegate reflection); the method table is enforced here.
    private static async Task HandleAsync(HttpContext http)
    {
        if (!HttpMethods.IsPost(http.Request.Method))
        {
            http.Response.Headers.Allow = "POST";
            http.Response.StatusCode = StatusCodes.Status405MethodNotAllowed;
            return;
        }

        await (await HandoffAsync(http)).ExecuteAsync(http);
    }

    private static async Task<IResult> HandoffAsync(HttpContext http)
    {
        var services = http.RequestServices;
        var options = services.GetService<SignInOptions>() ?? SignInOptions.Default;
        if (!options.Enabled) return Error(StatusCodes.Status404NotFound, "not_found");

        var remote = http.Connection.RemoteIpAddress;
        if (!http.Request.IsHttps && !options.AllowPlaintextOverlay && remote is not null && !IPAddress.IsLoopback(remote))
            return Error(StatusCodes.Status403Forbidden, "forbidden");

        var body = await ReadAsync(http);
        if (body is null
            || string.IsNullOrWhiteSpace(body.Code)
            || string.IsNullOrWhiteSpace(body.DeviceClass))
            return Error(StatusCodes.Status400BadRequest, "malformed_request");

        // Checked before the code is touched, so an unavailable service does not burn a code the client could retry.
        var tokens = services.GetService<ITokenService>();
        var codes = services.GetService<IHandoffCodeStore>();
        if (tokens is null || codes is null) return Error(StatusCodes.Status503ServiceUnavailable, "unavailable");

        var logger = services.GetService<ILoggerFactory>()?.CreateLogger(_logCategory);
        var time = services.GetService<TimeProvider>() ?? TimeProvider.System;

        SignInIdentity? identity = null;
        if (body.Code.Length <= _maxCodeLength)
            identity = await codes.RedeemAsync(body.Code, SignInAudiences.Native, time.GetUtcNow(), http.RequestAborted);
        if (identity is null)
        {
            if (logger is not null) AuthHandoffLog.CodeRefused(logger, body.Code.Length <= _maxCodeLength ? "unknown, used, expired or wrong audience" : "malformed");
            return Error(StatusCodes.Status401Unauthorized, "invalid_grant");
        }

        var device = new DeviceInfo(identity.DeviceId.ToString("D"), body.DeviceClass, body.ClientBuild ?? string.Empty);
        try
        {
            var pair = await tokens.IssueAsync(identity.DeviceId, identity.PrincipalId, device, http.RequestAborted);
            var expiresIn = Math.Max(0, (int)(pair.AccessTokenExpiresAt - time.GetUtcNow()).TotalSeconds);
            return Results.Json(
                new HandoffTokenResponse(pair.AccessToken, "Bearer", expiresIn, pair.RefreshToken),
                AuthHandoffWireContext.Default.HandoffTokenResponse,
                contentType: "application/json",
                statusCode: StatusCodes.Status200OK);
        }
        catch (TokenIssueException refused)
        {
            if (logger is not null) AuthHandoffLog.IssueRefused(logger, refused.Failure);
            return Error(StatusCodes.Status401Unauthorized, "invalid_grant");
        }
        catch (AccessTokenIssuerUnavailableException)
        {
            // The code is already burned: the person signs in again. No credential was stored.
            return Error(StatusCodes.Status503ServiceUnavailable, "unavailable");
        }
    }

    private static async Task<HandoffRequest?> ReadAsync(HttpContext http)
    {
        if (http.Request.ContentLength is > _maxBodyBytes) return null;
        try
        {
            using var ms = new MemoryStream();
            var buffer = new byte[1024];
            int n;
            while ((n = await http.Request.Body.ReadAsync(buffer, http.RequestAborted)) > 0)
            {
                if (ms.Length + n > _maxBodyBytes) return null;
                ms.Write(buffer, 0, n);
            }

            ms.Position = 0;
            return await JsonSerializer.DeserializeAsync(ms, AuthHandoffWireContext.Default.HandoffRequest, http.RequestAborted);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static IResult Error(int status, string code) =>
        Results.Json(new HandoffError(code), AuthHandoffWireContext.Default.HandoffError, contentType: "application/json", statusCode: status);
}

/// <summary>Request body for <c>POST /auth/handoff</c>.</summary>
public sealed record HandoffRequest(
    [property: JsonPropertyName("code")] string? Code,
    [property: JsonPropertyName("device_class")] string? DeviceClass,
    [property: JsonPropertyName("client_build")] string? ClientBuild);

/// <summary>The token pair the handoff route returns: the same body as <c>POST /auth/refresh</c> (doc 32 section 5).</summary>
public sealed record HandoffTokenResponse(
    [property: JsonPropertyName("access_token")] string AccessToken,
    [property: JsonPropertyName("token_type")] string TokenType,
    [property: JsonPropertyName("expires_in")] int ExpiresIn,
    [property: JsonPropertyName("refresh_token")] string RefreshToken);

/// <summary>The error body, <c>{"error": "..."}</c>, as on the refresh route.</summary>
public sealed record HandoffError([property: JsonPropertyName("error")] string Error);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.Unspecified)]
[JsonSerializable(typeof(HandoffRequest))]
[JsonSerializable(typeof(HandoffTokenResponse))]
[JsonSerializable(typeof(HandoffError))]
internal sealed partial class AuthHandoffWireContext : JsonSerializerContext;

/// <summary>Log lines for the handoff route. The reason is logged; the code never is.</summary>
internal static partial class AuthHandoffLog
{
    [LoggerMessage(Level = LogLevel.Information, Message = "Handoff code refused: {Reason}.")]
    public static partial void CodeRefused(ILogger logger, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "Handoff redeemed but device credential refused: {Reason}.")]
    public static partial void IssueRefused(ILogger logger, TokenIssueFailure reason);
}
