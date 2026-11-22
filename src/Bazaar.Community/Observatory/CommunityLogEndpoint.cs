using System.Globalization;
using Bazaar.Contracts.Community;
using Bazaar.Server.Http;
using Bazaar.Server.Wire.Connection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Bazaar.Community.Observatory;

/// <summary>
/// <c>GET /community/log?agentId=&amp;platform=&amp;limit=&amp;cursor=</c> (doc 36 WP9, doc 02 HTTP surface rules). Same order of checks
/// as the other HTTP routes: TLS rule (403), bearer credential (401), the HTTP rate bucket (429), then the route's own rule. The
/// log is owner-only (doc 36: the Observatory shows it to the owner; doc 15 puts outward-action review with the owner), checked by
/// principal kind the way <c>/vault/search</c> reads it, so another principal gets 403. Rows carry the body hash and never the body.
/// Errors are a short code and never a stack trace or a path.
/// </summary>
public static class CommunityLogEndpoint
{
    /// <summary>Route path.</summary>
    public const string Path = "/community/log";

    /// <summary>Maps the route. Called by host composition, next to <c>MapBazaarHttpSurface</c>.</summary>
    public static IEndpointRouteBuilder MapCommunityLog(this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);
        routes.MapGet(Path, (Func<HttpContext, Task<IResult>>)HandleAsync);
        return routes;
    }

    /// <summary>Runs the route against one request. Public so tests can drive it without a server.</summary>
    public static async Task<IResult> HandleAsync(HttpContext http)
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

        if (auth.Principal.Kind != "owner") return Error(StatusCodes.Status403Forbidden, "forbidden");

        var query = http.Request.Query;
        var limit = CommunityLogFilter.DefaultLimit;
        if (query.TryGetValue("limit", out var limitText) && limitText.Count > 0
            && !int.TryParse(limitText.ToString(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out limit))
            return Error(StatusCodes.Status400BadRequest, "bad_request");

        CommunityLogPosition? after = null;
        var cursor = query["cursor"].ToString();
        if (cursor.Length > 0)
        {
            if (!CommunityLogCursor.TryDecode(cursor, out var position)) return Error(StatusCodes.Status400BadRequest, "bad_cursor");
            after = position;
        }

        var reader = services.GetService<ICommunityLogReader>();
        if (reader is null) return Error(StatusCodes.Status503ServiceUnavailable, "unavailable");

        var filter = new CommunityLogFilter(NullIfEmpty(query["agentId"].ToString()), NullIfEmpty(query["platform"].ToString()), limit, after);
        var page = await reader.ListPageAsync(filter, http.RequestAborted);

        var dto = new CommunityLogPageDto(
            [.. page.Rows.Select(ToDto)],
            page.Next is { } next ? CommunityLogCursor.Encode(next) : null);
        return Results.Text(
            System.Text.Json.JsonSerializer.Serialize(dto, CommunityContractsJsonContext.Default.CommunityLogPageDto),
            "application/json");
    }

    private static CommunityLogRowDto ToDto(CommunityLogRow r) => new(
        r.Id, r.AgentId, r.Platform, r.Action.ToString(), r.TargetUrl, r.BodySha256Hex, r.Status, r.CreatedAt, r.ApprovalId, r.ExternalId);

    private static string? NullIfEmpty(string value) => value.Length == 0 ? null : value;

    private static IResult Unauthorized(HttpContext http)
    {
        http.Response.Headers.WWWAuthenticate = "Bearer";
        return Error(StatusCodes.Status401Unauthorized, "unauthorized");
    }

    private static IResult Error(int status, string code) => Results.Json(new { error = code }, statusCode: status);
}
