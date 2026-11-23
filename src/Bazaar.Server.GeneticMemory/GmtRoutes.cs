using System.Text.Json;
using System.Text.Json.Serialization;
using Bazaar.Server.GeneticMemory.Abstractions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Bazaar.Server.GeneticMemory;

/// <summary>Decides who the caller of a review route is. Returns null for anyone who may not review. The host provides it; without one every route answers 403.</summary>
public interface IGmtReviewerResolver
{
    /// <summary>The reviewer role of the authenticated caller, or null.</summary>
    Task<GmtReviewer?> ResolveAsync(HttpContext http, CancellationToken ct);
}

/// <summary>One candidate on the wire (JSON, camel case).</summary>
public sealed record GmtQueueItemDto(
    Guid Id, string ChildAgent, string TargetAgent, string ContentClass, string Kind, string Title, string Body,
    string? TraceRef, bool Tainted, bool OwnerOnly, DateTimeOffset StagedAt);

/// <summary>Accept body: a null title or body keeps the staged text.</summary>
public sealed record GmtAcceptDto(string? Title, string? Body);

/// <summary>Answer to accept.</summary>
public sealed record GmtAcceptedDto(string? Path);

/// <summary>Error answer. Codes only; no matched text, no paths.</summary>
public sealed record GmtErrorDto(string Error);

[JsonSerializable(typeof(GmtQueueItemDto[]))]
[JsonSerializable(typeof(GmtAcceptDto))]
[JsonSerializable(typeof(GmtAcceptedDto))]
[JsonSerializable(typeof(GmtErrorDto))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.Never)]
internal sealed partial class GmtJson : JsonSerializerContext;

/// <summary>
/// [PROPOSED] Review queue routes behind the client panel (doc 25: "Review queue: candidate, trace link, accept, edit or reject").
/// <c>GET /gmt/queue</c>, <c>POST /gmt/queue/{id}/accept</c> (body <see cref="GmtAcceptDto"/>) and <c>POST /gmt/queue/{id}/reject</c>.
/// The reviewer role comes only from <see cref="IGmtReviewerResolver"/>, never from the request, so a client cannot claim the owner.
/// With no resolver, or one that returns null, every route answers 403 and nothing is read or changed. The caller's authentication,
/// transport and rate limit are the host's <paramref name="guard"/>. Errors carry a code only.
/// </summary>
public static class GmtRoutes
{
    private const int _maxBodyBytes = 64 * 1024;

    /// <summary>Maps the three routes. <paramref name="guard"/> wraps each (authentication, rate limit).</summary>
    public static IEndpointRouteBuilder MapBazaarGeneticMemory(this IEndpointRouteBuilder app, Func<RouteHandlerBuilder, RouteHandlerBuilder> guard)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(guard);

        guard(app.MapGet("/gmt/queue", async Task<IResult> (HttpContext http) =>
            await WithReviewerAsync(http, async (service, role) =>
            {
                var items = await service.QueueAsync(role, http.RequestAborted).ConfigureAwait(false);
                return Results.Json([.. items.Select(ToDto)], GmtJson.Default.GmtQueueItemDtoArray);
            })));

        guard(app.MapPost("/gmt/queue/{id}/accept", async Task<IResult> (HttpContext http, string id) =>
            await WithReviewerAsync(http, async (service, role) =>
            {
                if (!Guid.TryParse(id, out var guid))
                {
                    return Error("bad_request", StatusCodes.Status400BadRequest);
                }

                var body = await ReadBodyAsync(http).ConfigureAwait(false);
                if (body is null)
                {
                    return Error("bad_request", StatusCodes.Status400BadRequest);
                }

                var outcome = await service.AcceptAsync(guid, role, body.Title, body.Body, http.RequestAborted).ConfigureAwait(false);
                return outcome.Succeeded
                    ? Results.Json(new GmtAcceptedDto(outcome.Path), GmtJson.Default.GmtAcceptedDto)
                    : Refused(outcome);
            })));

        guard(app.MapPost("/gmt/queue/{id}/reject", async Task<IResult> (HttpContext http, string id) =>
            await WithReviewerAsync(http, async (service, role) =>
            {
                if (!Guid.TryParse(id, out var guid))
                {
                    return Error("bad_request", StatusCodes.Status400BadRequest);
                }

                var outcome = await service.RejectAsync(guid, role, http.RequestAborted).ConfigureAwait(false);
                return outcome.Succeeded ? Results.Json(new GmtAcceptedDto(null), GmtJson.Default.GmtAcceptedDto) : Refused(outcome);
            })));

        return app;
    }

    private static async Task<IResult> WithReviewerAsync(HttpContext http, Func<IGeneticMemoryTransfer, GmtReviewer, Task<IResult>> run)
    {
        var resolver = http.RequestServices.GetService<IGmtReviewerResolver>();
        var service = http.RequestServices.GetService<IGeneticMemoryTransfer>();
        if (resolver is null || service is null)
        {
            return Error("forbidden", StatusCodes.Status403Forbidden);
        }

        var role = await resolver.ResolveAsync(http, http.RequestAborted).ConfigureAwait(false);
        return role is { } reviewer ? await run(service, reviewer).ConfigureAwait(false) : Error("forbidden", StatusCodes.Status403Forbidden);
    }

    private static async Task<GmtAcceptDto?> ReadBodyAsync(HttpContext http)
    {
        if (http.Request.ContentLength > _maxBodyBytes)
        {
            return null;
        }

        // A body may arrive chunked (no Content-Length), so read at most the limit plus one byte and refuse anything longer.
        using var buffer = new MemoryStream();
        var chunk = new byte[4096];
        int read;
        while ((read = await http.Request.Body.ReadAsync(chunk, http.RequestAborted).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > _maxBodyBytes)
            {
                return null;
            }

            buffer.Write(chunk, 0, read);
        }

        if (buffer.Length == 0)
        {
            return new GmtAcceptDto(null, null);
        }

        try
        {
            return JsonSerializer.Deserialize(buffer.ToArray(), GmtJson.Default.GmtAcceptDto);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static GmtQueueItemDto ToDto(GmtCandidate c) => new(
        c.Id, c.ChildAgent, c.TargetAgent, c.ContentClass.ToString(), c.Kind.ToString(), c.Title, c.Body, c.TraceRef, c.Tainted,
        c.RequiredReviewer == GmtReviewer.Owner, c.StagedAt);

    private static IResult Refused(GmtOutcome outcome) => outcome.Refusal switch
    {
        GmtRefusal.NotFound => Error("not_found", StatusCodes.Status404NotFound),
        GmtRefusal.AlreadyDecided => Error("already_decided", StatusCodes.Status409Conflict),
        GmtRefusal.ReviewerNotAllowed => Error("forbidden", StatusCodes.Status403Forbidden),
        GmtRefusal.SecretFound => Error("secret_found", StatusCodes.Status422UnprocessableEntity),
        GmtRefusal.PersonalData => Error("personal_data", StatusCodes.Status422UnprocessableEntity),
        GmtRefusal.PathInvalid or GmtRefusal.InvalidInput => Error("invalid", StatusCodes.Status422UnprocessableEntity),
        _ => Error("unavailable", StatusCodes.Status502BadGateway),
    };

    private static IResult Error(string code, int status) =>
        Results.Json(new GmtErrorDto(code), GmtJson.Default.GmtErrorDto, statusCode: status);
}
