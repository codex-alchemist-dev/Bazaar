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
/// <c>GET /auth/whoami</c> (doc 32 section 5): a signed-in client sends its access token and reads who it is signed in as. Every refusal
/// (absent, malformed, expired, badly signed, revoked device, suspended or removed human) answers the same 401 <c>invalid_token</c>; the
/// reason goes to the server log only and the token is never logged. Same transport rule as <c>/auth/refresh</c>. Read-only, no audit entry.
/// </summary>
public static class AuthWhoAmIEndpoint
{
    private const string _logCategory = "Bazaar.Server.Host.Auth.WhoAmI";

    /// <summary>Maps <c>GET /auth/whoami</c>. The verifier is looked up per request, so the route answers 503 until one is registered.</summary>
    public static IEndpointRouteBuilder MapBazaarAuthWhoAmI(this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);
        routes.MapGet("/auth/whoami", (Func<HttpContext, Task<IResult>>)HandleAsync);
        return routes;
    }

    internal static async Task<IResult> HandleAsync(HttpContext http)
    {
        if (TransportRefusal(http)) return Error(StatusCodes.Status403Forbidden, "forbidden");

        var services = http.RequestServices;
        var verifier = services.GetService<IAccessTokenVerifier>();
        var lookup = services.GetService<SignedInPersonLookup>();
        if (verifier is null || lookup is null) return Error(StatusCodes.Status503ServiceUnavailable, "unavailable");

        var logger = services.GetService<ILoggerFactory>()?.CreateLogger(_logCategory);
        var token = AuthenticatedCallerMiddleware.BearerToken(http.Request.Headers.Authorization.ToString());
        if (token is null) return Refused(logger, WhoAmIRefusal.NoBearerToken);
        if (!verifier.TryVerify(token, out var caller)) return Refused(logger, WhoAmIRefusal.TokenNotValid);

        var person = await lookup.FindAsync(caller.DeviceId, caller.HumanId, http.RequestAborted).ConfigureAwait(false);
        if (person is null) return Refused(logger, WhoAmIRefusal.DeviceOrHumanNotActive);

        return Results.Json(
            new WhoAmIBody(person.HumanId, person.DisplayName, person.Role, person.DeviceId),
            AuthWhoAmIWireContext.Default.WhoAmIBody,
            contentType: "application/json",
            statusCode: StatusCodes.Status200OK);
    }

    private static IResult Refused(ILogger? logger, WhoAmIRefusal reason)
    {
        if (logger is not null) AuthWhoAmILog.Refused(logger, reason);
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
        Results.Json(new WhoAmIError(code), AuthWhoAmIWireContext.Default.WhoAmIError, contentType: "application/json", statusCode: status);
}

/// <summary>Why a whoami request was refused. Logged only; the client sees one generic <c>invalid_token</c>.</summary>
internal enum WhoAmIRefusal
{
    NoBearerToken,
    TokenNotValid,
    DeviceOrHumanNotActive,
}

/// <summary>The 200 body of <c>GET /auth/whoami</c>.</summary>
public sealed record WhoAmIBody(
    [property: JsonPropertyName("human_id")] Guid HumanId,
    [property: JsonPropertyName("display_name")] string DisplayName,
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("device_id")] Guid DeviceId);

/// <summary>The error body of the whoami refusals.</summary>
public sealed record WhoAmIError([property: JsonPropertyName("error")] string Error);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.Unspecified)]
[JsonSerializable(typeof(WhoAmIBody))]
[JsonSerializable(typeof(WhoAmIError))]
internal sealed partial class AuthWhoAmIWireContext : JsonSerializerContext;

internal static partial class AuthWhoAmILog
{
    [LoggerMessage(Level = LogLevel.Information, Message = "whoami refused: {Reason}.")]
    public static partial void Refused(ILogger logger, WhoAmIRefusal reason);
}
