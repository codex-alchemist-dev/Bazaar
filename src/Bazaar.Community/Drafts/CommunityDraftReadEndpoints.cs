using System.Globalization;
using System.Text.Json;
using Bazaar.Community.Http;
using Bazaar.Contracts.Community;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Bazaar.Community.Drafts;

/// <summary>
/// <c>GET /community/drafts/{id}</c> and <c>GET /community/drafts?status=pending&amp;limit=n</c> (doc 36 WP8) [PROPOSED]. The owner's
/// clients read the draft-review card here before signing a decision. Same guard order as the decision route: plaintext 403,
/// bearer 401, rate limit 429, owner-only 403. Answers: 200 with a <see cref="DraftReviewCardDto"/> or <see cref="DraftReviewListDto"/>,
/// 400 bad id or query, 404 unknown draft, 503 unavailable. Errors are a short code; bodies are never logged.
/// </summary>
public static class CommunityDraftReadEndpoints
{
    /// <summary>Largest page of the list.</summary>
    public const int MaxLimit = 100;

    /// <summary>Page size when <c>limit</c> is not given.</summary>
    public const int DefaultLimit = 50;

    /// <summary>Maps both routes. Called by host composition, next to <c>MapCommunityDraftDecision</c>.</summary>
    public static IEndpointRouteBuilder MapCommunityDraftReads(this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);
        routes.MapGet("/" + CommunityRoutes.DraftTemplate, (Func<HttpContext, Task<IResult>>)GetAsync);
        routes.MapGet("/" + CommunityRoutes.DraftList, (Func<HttpContext, Task<IResult>>)ListAsync);
        return routes;
    }

    /// <summary>Runs <c>GET /community/drafts/{id}</c> against one request. Public so tests can drive it without a server.</summary>
    public static async Task<IResult> GetAsync(HttpContext http)
    {
        var denied = await CommunityHttpGuard.CheckOwnerAsync(http);
        if (denied is not null) return denied;

        if (!Guid.TryParse(http.Request.RouteValues["id"]?.ToString(), out var draftId) || draftId == Guid.Empty)
            return CommunityHttpGuard.Error(StatusCodes.Status400BadRequest, "bad_request");

        if (!TryResolve(http, out var store, out var handler)) return CommunityHttpGuard.Error(StatusCodes.Status503ServiceUnavailable, "unavailable");

        var draft = await store.GetAsync(draftId, http.RequestAborted);
        return draft is null
            ? CommunityHttpGuard.Error(StatusCodes.Status404NotFound, "unknown_draft")
            : Json(handler.ToCard(draft));
    }

    /// <summary>Runs <c>GET /community/drafts</c> against one request. Only <c>status=pending</c> (the default) is served.</summary>
    public static async Task<IResult> ListAsync(HttpContext http)
    {
        var denied = await CommunityHttpGuard.CheckOwnerAsync(http);
        if (denied is not null) return denied;

        var status = http.Request.Query["status"].ToString();
        if (status.Length > 0 && !string.Equals(status, "pending", StringComparison.OrdinalIgnoreCase))
            return CommunityHttpGuard.Error(StatusCodes.Status400BadRequest, "bad_request");

        var limit = DefaultLimit;
        var limitText = http.Request.Query["limit"].ToString();
        if (limitText.Length > 0
            && (!int.TryParse(limitText, NumberStyles.None, CultureInfo.InvariantCulture, out limit) || limit is < 1 or > MaxLimit))
            return CommunityHttpGuard.Error(StatusCodes.Status400BadRequest, "bad_request");

        if (!TryResolve(http, out var store, out var handler)) return CommunityHttpGuard.Error(StatusCodes.Status503ServiceUnavailable, "unavailable");

        var now = http.RequestServices.GetService<TimeProvider>()?.GetUtcNow() ?? DateTimeOffset.UtcNow;
        var open = await store.ListPendingAsync(now, limit, http.RequestAborted);
        return Json(new DraftReviewListDto([.. open.Select(handler.ToCard)]));
    }

    private static bool TryResolve(HttpContext http, out ICommunityDraftStore store, out CommunityDraftDecisionHandler handler)
    {
        store = null!;
        handler = null!;
        try
        {
            // The handler is registered by a factory that needs host-supplied services; a missing one means "not configured".
            var s = http.RequestServices.GetService<ICommunityDraftStore>();
            var h = http.RequestServices.GetService<CommunityDraftDecisionHandler>();
            if (s is null || h is null) return false;
            store = s;
            handler = h;
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static IResult Json(DraftReviewCardDto card) =>
        Results.Text(JsonSerializer.Serialize(card, CommunityContractsJsonContext.Default.DraftReviewCardDto), "application/json");

    private static IResult Json(DraftReviewListDto list) =>
        Results.Text(JsonSerializer.Serialize(list, CommunityContractsJsonContext.Default.DraftReviewListDto), "application/json");
}
