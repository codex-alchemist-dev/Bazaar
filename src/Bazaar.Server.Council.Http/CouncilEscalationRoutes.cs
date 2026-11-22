using System.Text.Json;
using System.Text.Json.Serialization;
using Bazaar.Server.Council.Escalation;
using Bazaar.Server.Council.Service;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Matching;
using Microsoft.Extensions.DependencyInjection;

namespace Bazaar.Server.Council.Http;

/// <summary>The host's authentication wrapper: runs the handler with the caller's id and kind, or answers the refusal itself.</summary>
public delegate Task<IResult> CouncilGuard(HttpContext http, Func<HttpContext, Guid, string, Task<IResult>> run);

/// <summary>
/// Owner-only HTTP routes for escalated Council votes (docs/design/10-council-and-voting.md, Escalation):
/// <c>GET /council/escalations</c> lists the votes awaiting the owner, and <c>POST /council/escalations/{voteId}/resolve</c>
/// with <c>{"choice":"...","reasoning":"..."}</c> resolves one. A caller who is not the owner gets 403; an unknown or no longer
/// escalated vote 404 or 409; a bad choice 422; a missing service 503. This is the server side of the client's
/// <c>ResolveEscalation</c> API.
/// </summary>
public static class CouncilEscalationRoutes
{
    private const int _maxBodyBytes = 64 * 1024;

    public static IEndpointRouteBuilder MapCouncilEscalations(this IEndpointRouteBuilder routes, CouncilGuard guarded)
    {
        ArgumentNullException.ThrowIfNull(routes);
        ArgumentNullException.ThrowIfNull(guarded);

        Map(routes, "/council/escalations", "GET", guarded, ListAsync);
        Map(routes, "/council/escalations/{voteId}/resolve", "POST", guarded, ResolveAsync);
        return routes;
    }

    private static void Map(IEndpointRouteBuilder routes, string path, string method, CouncilGuard guarded, Func<HttpContext, Task<IResult>> handler)
    {
        RequestDelegate del = async http =>
        {
            var result = await guarded(http, (h, _, kind) =>
                kind == "owner" ? handler(h) : Task.FromResult(Error(StatusCodes.Status403Forbidden, "forbidden")));
            await result.ExecuteAsync(http);
        };
        routes.Map(path, del).WithMetadata(new HttpMethodMetadata([method]));
    }

    private static IResult Error(int status, string code) => Results.Text($$"""{"error":"{{code}}"}""", "application/json", statusCode: status);

    private static async Task<IResult> ListAsync(HttpContext http)
    {
        if (http.RequestServices.GetService<CouncilEscalationService>() is not { } service)
        {
            return Error(StatusCodes.Status503ServiceUnavailable, "not_configured");
        }

        var now = http.RequestServices.GetService<TimeProvider>()?.GetUtcNow() ?? DateTimeOffset.UtcNow;
        var votes = await service.ListAwaitingAsync(http.RequestAborted);
        var rows = votes.Select(v => new EscalationRow(
            v.VoteId,
            v.GcId,
            v.SubjectRef,
            v.Kind.ToString(),
            v.MotionClass.ToString(),
            [.. v.Options],
            v.EligibleCount,
            v.IndependentBallotCount,
            v.BallotsRevealed,
            [.. v.Ballots.Select(b => new BallotRow(b.VoterId, b.Phase.ToString(), b.Choice, b.Reasoning))],
            v.EscalationReason,
            v.EscalatedAt,
            v.DeadlineAt,
            (long)Math.Max(0, (now - v.EscalatedAt).TotalSeconds)));
        return Results.Json(new EscalationList([.. rows]), CouncilHttpJson.Default.EscalationList);
    }

    private static async Task<IResult> ResolveAsync(HttpContext http)
    {
        if (http.RequestServices.GetService<CouncilEscalationService>() is not { } service)
        {
            return Error(StatusCodes.Status503ServiceUnavailable, "not_configured");
        }

        if (!Guid.TryParse(http.Request.RouteValues["voteId"]?.ToString(), out var voteId))
        {
            return Error(StatusCodes.Status400BadRequest, "bad_vote_id");
        }

        if (http.Request.ContentLength is > _maxBodyBytes)
        {
            return Error(StatusCodes.Status413PayloadTooLarge, "too_large");
        }

        ResolveRequest? body;
        try
        {
            body = await JsonSerializer.DeserializeAsync(http.Request.Body, CouncilHttpJson.Default.ResolveRequest, http.RequestAborted);
        }
        catch (JsonException)
        {
            return Error(StatusCodes.Status400BadRequest, "bad_body");
        }

        if (body is null || string.IsNullOrWhiteSpace(body.Choice))
        {
            return Error(StatusCodes.Status400BadRequest, "bad_body");
        }

        if (await service.ViewAsync(voteId, http.RequestAborted) is null)
        {
            return Error(StatusCodes.Status404NotFound, "not_escalated");
        }

        try
        {
            await service.ResolveAsync(voteId, body.Choice, body.Reasoning, http.RequestAborted);
        }
        catch (CouncilRejectedException)
        {
            return Error(StatusCodes.Status422UnprocessableEntity, "rejected");
        }
        catch (ConcurrencyException)
        {
            return Error(StatusCodes.Status409Conflict, "busy");
        }

        return Results.Json(new ResolveResult(voteId, body.Choice), CouncilHttpJson.Default.ResolveResult);
    }
}

internal sealed record BallotRow(string VoterId, string Phase, string Choice, string Reasoning);

internal sealed record EscalationRow(
    Guid VoteId,
    Guid GcId,
    string Subject,
    string Kind,
    string MotionClass,
    string[] Options,
    int EligibleCount,
    int IndependentBallotCount,
    bool BallotsRevealed,
    BallotRow[] Ballots,
    string? EscalationReason,
    DateTimeOffset EscalatedAt,
    DateTimeOffset DeadlineAt,
    long AgeSeconds);

internal sealed record EscalationList(EscalationRow[] Escalations);

internal sealed record ResolveRequest(string Choice, string? Reasoning);

internal sealed record ResolveResult(Guid VoteId, string Choice);

[JsonSerializable(typeof(EscalationList))]
[JsonSerializable(typeof(ResolveRequest))]
[JsonSerializable(typeof(ResolveResult))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
internal sealed partial class CouncilHttpJson : JsonSerializerContext;
