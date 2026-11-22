using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Matching;
using Microsoft.Extensions.DependencyInjection;

namespace Bazaar.Server.CompanyMemory;

/// <summary>The JSON body of <c>POST /company-memory/decisions</c>: a decision without its recorder, who is the caller.</summary>
public sealed record RecordDecisionBody(
    Guid DocId,
    string Slug,
    string Title,
    string Body,
    IReadOnlyList<string>? Supersedes = null,
    string? DecidedIn = null);

/// <summary>
/// HTTP routes for Company Memory (doc 25): <c>POST /company-memory/decisions</c> records a decision and
/// <c>GET /company-memory/decisions/{reference}/chain</c> returns its chain. Bodies are JSON (camelCase).
/// </summary>
/// <remarks>
/// The host supplies <c>guarded</c>: it authenticates the caller, applies its limits and then runs the handler with the
/// caller's id, or answers 401, 429 and the like itself. The recording agent is always that id; a body cannot name another.
/// Bad input answers 400, an unknown decision 404.
/// </remarks>
/// <summary>
/// The host's authentication wrapper: runs <paramref name="run"/> with the authenticated caller's id, or answers the refusal itself.
/// </summary>
public delegate Task<IResult> CompanyMemoryGuard(HttpContext http, Func<HttpContext, string, Task<IResult>> run);

public static class CompanyMemoryRoutes
{
    /// <summary>Maps both routes onto <paramref name="routes"/>.</summary>
    public static IEndpointRouteBuilder MapCompanyMemory(
        this IEndpointRouteBuilder routes,
        CompanyMemoryGuard guarded)
    {
        ArgumentNullException.ThrowIfNull(routes);
        ArgumentNullException.ThrowIfNull(guarded);

        // RequestDelegates with method metadata keep the routes free of reflection (AOT-safe).
        RequestDelegate record = async http =>
        {
            var result = await guarded(http, RecordAsync);
            await result.ExecuteAsync(http);
        };
        RequestDelegate chain = async http =>
        {
            var result = await guarded(http, ChainAsync);
            await result.ExecuteAsync(http);
        };
        routes.Map("/company-memory/decisions", record).WithMetadata(new HttpMethodMetadata(["POST"]));
        routes.Map("/company-memory/decisions/{**reference}", chain).WithMetadata(new HttpMethodMetadata(["GET"]));
        return routes;
    }

    private static async Task<IResult> RecordAsync(HttpContext http, string callerId)
    {
        RecordDecisionBody? body;
        try
        {
            body = await JsonSerializer.DeserializeAsync(http.Request.Body, CompanyMemoryJson.Default.RecordDecisionBody, http.RequestAborted);
        }
        catch (JsonException)
        {
            return Results.BadRequest();
        }

        if (body is null)
        {
            return Results.BadRequest();
        }

        var request = new RecordDecisionRequest(body.DocId, body.Slug, body.Title, body.Body, callerId, body.Supersedes, body.DecidedIn);

        try
        {
            var memory = http.RequestServices.GetRequiredService<ICompanyMemory>();
            var recorded = await memory.RecordDecisionAsync(request, http.RequestAborted);
            return Results.Text(JsonSerializer.Serialize(recorded, CompanyMemoryJson.Default.RecordedDecision), "application/json");
        }
        catch (ArgumentException exception)
        {
            return Results.Text(exception.Message, "text/plain", statusCode: StatusCodes.Status400BadRequest);
        }
    }

    private static async Task<IResult> ChainAsync(HttpContext http, string callerId)
    {
        // The reference is a catch-all so a path such as decisions/0001-x.md also works; the last segment is "chain".
        var raw = http.Request.RouteValues["reference"]?.ToString() ?? string.Empty;
        const string suffix = "/chain";
        if (!raw.EndsWith(suffix, StringComparison.Ordinal) || raw.Length == suffix.Length)
        {
            return Results.NotFound();
        }

        var memory = http.RequestServices.GetRequiredService<ICompanyMemory>();
        var view = await memory.GetChainAsync(raw[..^suffix.Length], http.RequestAborted);
        return view is null
            ? Results.NotFound()
            : Results.Text(JsonSerializer.Serialize(view, CompanyMemoryJson.Default.DecisionChainView), "application/json");
    }
}
