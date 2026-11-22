using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Matching;
using Microsoft.Extensions.DependencyInjection;

namespace Bazaar.Server.CompanyMemory;

/// <summary>
/// HTTP routes for Company Memory (doc 25): <c>POST /company-memory/decisions</c> records a decision and
/// <c>GET /company-memory/decisions/{reference}/chain</c> returns its chain. Bodies are JSON (camelCase).
/// </summary>
/// <remarks>
/// The guard runs first, as in the other module routes: a non-null result is returned as it is, so the host decides who may
/// call. The recording agent comes from the request body; the host's guard is responsible for authenticating the caller.
/// Bad input answers 400, an unknown decision 404.
/// </remarks>
public static class CompanyMemoryRoutes
{
    /// <summary>Maps both routes onto <paramref name="routes"/>.</summary>
    public static IEndpointRouteBuilder MapCompanyMemory(
        this IEndpointRouteBuilder routes,
        Func<HttpContext, ValueTask<IResult?>> guard)
    {
        ArgumentNullException.ThrowIfNull(routes);
        ArgumentNullException.ThrowIfNull(guard);

        // RequestDelegates with method metadata keep the routes free of reflection (AOT-safe).
        RequestDelegate record = async http =>
        {
            var result = await RecordAsync(http, guard);
            await result.ExecuteAsync(http);
        };
        RequestDelegate chain = async http =>
        {
            var result = await ChainAsync(http, guard);
            await result.ExecuteAsync(http);
        };
        routes.Map("/company-memory/decisions", record).WithMetadata(new HttpMethodMetadata(["POST"]));
        routes.Map("/company-memory/decisions/{**reference}", chain).WithMetadata(new HttpMethodMetadata(["GET"]));
        return routes;
    }

    private static async Task<IResult> RecordAsync(HttpContext http, Func<HttpContext, ValueTask<IResult?>> guard)
    {
        if (await guard(http) is { } refused)
        {
            return refused;
        }

        RecordDecisionRequest? request;
        try
        {
            request = await JsonSerializer.DeserializeAsync(http.Request.Body, CompanyMemoryJson.Default.RecordDecisionRequest, http.RequestAborted);
        }
        catch (JsonException)
        {
            return Results.BadRequest();
        }

        if (request is null)
        {
            return Results.BadRequest();
        }

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

    private static async Task<IResult> ChainAsync(HttpContext http, Func<HttpContext, ValueTask<IResult?>> guard)
    {
        if (await guard(http) is { } refused)
        {
            return refused;
        }

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
