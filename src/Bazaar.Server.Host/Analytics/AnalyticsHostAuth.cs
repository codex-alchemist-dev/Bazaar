using System.Globalization;
using Bazaar.Server.Analytics;
using Bazaar.Server.Http;
using Bazaar.Server.Wire.Connection;
using Microsoft.Extensions.Options;

namespace Bazaar.Server.Host.Analytics;

/// <summary>
/// Authenticates <c>GET /analytics</c> in the same order as the other HTTP routes: TLS rule (403), bearer credential (401), the HTTP
/// rate bucket (429). The principal kind then decides the viewer: <c>owner</c> is the owner, <c>agent</c> an agent; anything else
/// is a plain viewer who sees company totals only.
/// </summary>
internal static class AnalyticsHostAuth
{
    public static async Task<AnalyticsResolution> ResolveAsync(HttpContext http)
    {
        var services = http.RequestServices;
        var wire = services.GetRequiredService<IOptions<WireOptions>>().Value;

        var remote = http.Connection.RemoteIpAddress;
        if (!http.Request.IsHttps && !wire.AllowPlaintextOverlay && remote is not null && !System.Net.IPAddress.IsLoopback(remote))
        {
            return Fail(StatusCodes.Status403Forbidden, "forbidden");
        }

        var header = http.Request.Headers.Authorization.ToString();
        var token = header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? header[7..].Trim() : null;
        if (string.IsNullOrEmpty(token))
        {
            return Unauthorized(http);
        }

        var auth = await services.GetRequiredService<IConnectionAuthenticator>()
            .AuthenticateAsync(new HelloCredential(token), new DeviceInfo("http", "http", string.Empty), http.RequestAborted);
        if (!auth.Ok || auth.Principal is null || (auth.ExpiresAt is { } expiry && expiry <= services.GetRequiredService<TimeProvider>().GetUtcNow()))
        {
            return Unauthorized(http);
        }

        if (!services.GetRequiredService<HttpRateLimiter>().TryAcquire(auth.Principal.Id, out var retryAfter))
        {
            http.Response.Headers.RetryAfter = retryAfter.ToString(CultureInfo.InvariantCulture);
            return Fail(StatusCodes.Status429TooManyRequests, "rate_limited");
        }

        var kind = auth.Principal.Kind;
        return new AnalyticsResolution(new AnalyticsViewer(auth.Principal.Id.ToString(), kind == "owner", kind == "agent"));
    }

    private static AnalyticsResolution Unauthorized(HttpContext http)
    {
        http.Response.Headers.WWWAuthenticate = "Bearer";
        return Fail(StatusCodes.Status401Unauthorized, "unauthorized");
    }

    private static AnalyticsResolution Fail(int status, string code) => new(null, Results.Json(new { error = code }, statusCode: status));
}
