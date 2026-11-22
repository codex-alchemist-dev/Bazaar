using System.Text.Json.Serialization;
using Bazaar.Server.Components.Remote;
using Bazaar.Server.Http.Auth;
using Bazaar.Server.Wire.Connection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Bazaar.Server.Host.Auth;

/// <summary>
/// <c>POST /auth/signout</c> (doc 32 section 5): a signed-in client sends its access token and its device is revoked at once, so its
/// refresh secret stops working. Only the calling device is revoked. Every token refusal answers the same 401 <c>invalid_token</c> as
/// <c>/auth/whoami</c>; the reason goes to the server log only and the token is never logged. Signing out twice is not an error.
/// </summary>
public static class AuthSignOutEndpoint
{
    private const string _logCategory = "Bazaar.Server.Host.Auth.SignOut";

    /// <summary>Maps <c>POST /auth/signout</c>. Services are looked up per request, so the route answers 503 until they are registered.</summary>
    public static IEndpointRouteBuilder MapBazaarAuthSignOut(this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);
        routes.MapPost("/auth/signout", (Func<HttpContext, Task<IResult>>)HandleAsync);
        return routes;
    }

    internal static async Task<IResult> HandleAsync(HttpContext http)
    {
        if (TransportRefusal(http)) return Error(StatusCodes.Status403Forbidden, "forbidden");

        var services = http.RequestServices;
        var verifier = services.GetService<IAccessTokenVerifier>();
        var tokens = services.GetService<ITokenService>();
        if (verifier is null || tokens is null) return Error(StatusCodes.Status503ServiceUnavailable, "unavailable");

        var logger = services.GetService<ILoggerFactory>()?.CreateLogger(_logCategory);
        var token = AuthenticatedCallerMiddleware.BearerToken(http.Request.Headers.Authorization.ToString());
        if (token is null) return Refused(logger, SignOutRefusal.NoBearerToken);
        if (!verifier.TryVerify(token, out var caller)) return Refused(logger, SignOutRefusal.TokenNotValid);

        await tokens.RevokeDeviceAsync(caller.DeviceId, new PrincipalRef(caller.HumanId, "human"), http.RequestAborted).ConfigureAwait(false);
        return Results.NoContent();
    }

    private static IResult Refused(ILogger? logger, SignOutRefusal reason)
    {
        if (logger is not null) AuthSignOutLog.Refused(logger, reason);
        return Error(StatusCodes.Status401Unauthorized, "invalid_token");
    }

    /// <summary>The same transport rule as <c>/auth/refresh</c>: plaintext off the machine is refused unless the operator allowed an overlay.</summary>
    private static bool TransportRefusal(HttpContext http)
    {
        var wire = http.RequestServices.GetRequiredService<IOptions<WireOptions>>().Value;
        var remote = http.Connection.RemoteIpAddress;
        return !http.Request.IsHttps && !wire.AllowPlaintextOverlay && remote is not null && !System.Net.IPAddress.IsLoopback(remote);
    }

    private static IResult Error(int status, string code) =>
        Results.Json(new SignOutError(code), AuthSignOutWireContext.Default.SignOutError, contentType: "application/json", statusCode: status);
}

/// <summary>Why a sign-out request was refused. Logged only; the client sees one generic <c>invalid_token</c>.</summary>
internal enum SignOutRefusal
{
    NoBearerToken,
    TokenNotValid,
}

/// <summary>The error body of the sign-out refusals.</summary>
public sealed record SignOutError([property: JsonPropertyName("error")] string Error);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.Unspecified)]
[JsonSerializable(typeof(SignOutError))]
internal sealed partial class AuthSignOutWireContext : JsonSerializerContext;

internal static partial class AuthSignOutLog
{
    [LoggerMessage(Level = LogLevel.Information, Message = "sign-out refused: {Reason}.")]
    public static partial void Refused(ILogger logger, SignOutRefusal reason);
}
