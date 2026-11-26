using System.Globalization;
using Bazaar.Server.Http;
using Bazaar.Server.Wire.Connection;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Bazaar.Server.Community.Http;

/// <summary>
/// The guard order every owner-only community route shares (doc 02 HTTP surface rules): plaintext off-machine 403, bearer
/// credential 401, the HTTP rate bucket 429, then owner-only 403 by principal kind. Errors are a short code only.
/// </summary>
internal static class CommunityHttpGuard
{
    /// <summary>Runs the guard. Returns the failure result, or null when the caller is an authenticated owner within the rate limit.</summary>
    public static async Task<IResult?> CheckOwnerAsync(HttpContext http)
    {
        var services = http.RequestServices;
        var wire = services.GetRequiredService<IOptions<WireOptions>>().Value;

        // Credentials never travel in clear text off the machine unless the operator allowed an overlay network.
        var remote = http.Connection.RemoteIpAddress;
        if (!http.Request.IsHttps && !wire.AllowPlaintextOverlay && remote is not null && !System.Net.IPAddress.IsLoopback(remote))
            return Error(StatusCodes.Status403Forbidden, "forbidden");

        var header = http.Request.Headers.Authorization.ToString();
        var token = header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? header[7..].Trim() : null;
        if (string.IsNullOrEmpty(token)) return Unauthorized(http);

        var auth = await services.GetRequiredService<IConnectionAuthenticator>()
            .AuthenticateAsync(new HelloCredential(token), new DeviceInfo("http", "http", string.Empty), http.RequestAborted);
        if (!auth.Ok || auth.Principal is null || (auth.ExpiresAt is { } expiry && expiry <= services.GetRequiredService<TimeProvider>().GetUtcNow()))
            return Unauthorized(http);

        if (!services.GetRequiredService<HttpRateLimiter>().TryAcquire(auth.Principal.Id, out var retryAfter))
        {
            http.Response.Headers.RetryAfter = retryAfter.ToString(CultureInfo.InvariantCulture);
            return Error(StatusCodes.Status429TooManyRequests, "rate_limited");
        }

        return auth.Principal.Kind != "owner" ? Error(StatusCodes.Status403Forbidden, "forbidden") : null;
    }

    /// <summary>A JSON error body with a short code.</summary>
    public static IResult Error(int status, string code) => Results.Json(new { error = code }, statusCode: status);

    private static IResult Unauthorized(HttpContext http)
    {
        http.Response.Headers.WWWAuthenticate = "Bearer";
        return Error(StatusCodes.Status401Unauthorized, "unauthorized");
    }
}
