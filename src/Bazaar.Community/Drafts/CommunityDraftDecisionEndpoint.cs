using System.Text.Json;
using Bazaar.Community.Http;
using Bazaar.Contracts.Community;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Bazaar.Community.Drafts;

/// <summary>
/// <c>POST /community/drafts/{id}/decision</c> (doc 36 WP8, doc 15 approval flow). Same guard order as <c>GET /community/log</c>:
/// plaintext 403, bearer 401, rate limit 429, owner-only 403. Then the body (a <see cref="DraftReviewDecisionDto"/>) is read with a
/// size cap and handed to <see cref="CommunityDraftDecisionHandler"/>. Answers: 200 with a <see cref="CommunityDraftDecisionResultDto"/>,
/// 400 bad body, 403 bad signature, 404 unknown draft, 409 decided or expired, 413 too large, 502 platform refusal, 503 unavailable.
/// Errors are a short code; bodies are never logged or echoed.
/// </summary>
public static class CommunityDraftDecisionEndpoint
{
    /// <summary>Largest request body accepted, in bytes. An edited body is at most 60 000 characters.</summary>
    public const int MaxBodyBytes = 262_144;

    /// <summary>Maps the route. Called by host composition, next to <c>MapCommunityLog</c>.</summary>
    public static IEndpointRouteBuilder MapCommunityDraftDecision(this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);
        routes.MapPost("/" + CommunityRoutes.DraftDecisionTemplate, (Func<HttpContext, Task<IResult>>)HandleAsync);
        return routes;
    }

    /// <summary>Runs the route against one request. Public so tests can drive it without a server.</summary>
    public static async Task<IResult> HandleAsync(HttpContext http)
    {
        var denied = await CommunityHttpGuard.CheckOwnerAsync(http);
        if (denied is not null) return denied;

        if (!Guid.TryParse(http.Request.RouteValues["id"]?.ToString(), out var draftId))
            return CommunityHttpGuard.Error(StatusCodes.Status400BadRequest, "bad_request");

        if (http.Request.ContentLength is { } length && length > MaxBodyBytes)
            return CommunityHttpGuard.Error(StatusCodes.Status413PayloadTooLarge, "too_large");

        CommunityDraftDecisionHandler? handler;
        try
        {
            // The component registers the handler by a factory; a service the host has not bound yet makes it throw, meaning "not configured".
            handler = http.RequestServices.GetService<CommunityDraftDecisionHandler>();
        }
        catch (InvalidOperationException)
        {
            handler = null;
        }

        if (handler is null) return CommunityHttpGuard.Error(StatusCodes.Status503ServiceUnavailable, "unavailable");

        DraftReviewDecisionDto? dto;
        try
        {
            using var buffer = new MemoryStream();
            var chunk = new byte[8192];
            int read;
            while ((read = await http.Request.Body.ReadAsync(chunk, http.RequestAborted)) > 0)
            {
                if (buffer.Length + read > MaxBodyBytes)
                    return CommunityHttpGuard.Error(StatusCodes.Status413PayloadTooLarge, "too_large");
                buffer.Write(chunk, 0, read);
            }

            buffer.Position = 0;
            dto = await JsonSerializer.DeserializeAsync(buffer, CommunityContractsJsonContext.Default.DraftReviewDecisionDto, http.RequestAborted);
        }
        catch (JsonException)
        {
            return CommunityHttpGuard.Error(StatusCodes.Status400BadRequest, "bad_request");
        }

        var outcome = await handler.HandleAsync(draftId, dto, http.RequestAborted);
        if (outcome.Status == DraftDecisionStatus.Ok && outcome.Result is { } result)
        {
            return Results.Text(
                JsonSerializer.Serialize(result, CommunityContractsJsonContext.Default.CommunityDraftDecisionResultDto),
                "application/json");
        }

        var status = outcome.Status switch
        {
            DraftDecisionStatus.BadRequest => StatusCodes.Status400BadRequest,
            DraftDecisionStatus.Forbidden => StatusCodes.Status403Forbidden,
            DraftDecisionStatus.NotFound => StatusCodes.Status404NotFound,
            DraftDecisionStatus.Conflict => StatusCodes.Status409Conflict,
            DraftDecisionStatus.PostRefused => StatusCodes.Status502BadGateway,
            _ => StatusCodes.Status503ServiceUnavailable,
        };
        return CommunityHttpGuard.Error(status, outcome.Code);
    }
}
