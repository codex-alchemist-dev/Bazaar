using System.Globalization;
using Bazaar.Community.Http;
using Bazaar.Contracts.Community;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

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
    public const string Path = "/" + CommunityRoutes.Log;

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
        var denied = await CommunityHttpGuard.CheckOwnerAsync(http);
        if (denied is not null) return denied;
        var services = http.RequestServices;

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

    private static IResult Error(int status, string code) => CommunityHttpGuard.Error(status, code);
}
