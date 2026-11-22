using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Bazaar.Server.Walkthrough;

/// <summary>
/// The walkthrough routes (doc 45 section 2.9, PROPOSED): <c>GET /onboarding/walkthrough/facts</c>, <c>GET /prefs/walkthrough</c>
/// and <c>PUT /prefs/walkthrough</c>. Every route needs a signed-in person (401 otherwise) and answers for that person only. A route
/// answers 503 until its ports are registered. Bodies and tokens never reach a log line. Transport rules (https or loopback) and the
/// rate bucket are applied by the host that maps these routes.
/// </summary>
public static class WalkthroughEndpoints
{
    private const int _maxBodyBytes = 2048;
    private static readonly HashSet<string> _steps = new(StringComparer.Ordinal) { "Welcome", "CreateCompany", "MeetAgent", "FirstTask", "Done" };

    public static IEndpointRouteBuilder MapWalkthroughEndpoints(this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);
        // RequestDelegate routes keep the module trim- and AOT-safe; each names its method, so a wrong method answers 405.
        routes.Map("/onboarding/walkthrough/facts", Run(FactsAsync)).WithMetadata(new HttpMethodMetadata(["GET"]));
        routes.Map("/prefs/walkthrough", Run(GetAsync)).WithMetadata(new HttpMethodMetadata(["GET"]));
        routes.Map("/prefs/walkthrough", Run(PutAsync)).WithMetadata(new HttpMethodMetadata(["PUT"]));
        return routes;
    }

    private static RequestDelegate Run(Func<HttpContext, Task<IResult>> handler) =>
        async http => await (await handler(http)).ExecuteAsync(http);

    private static async Task<IResult> FactsAsync(HttpContext http)
    {
        var (person, refusal) = await CallerAsync(http);
        if (person is null) return refusal!;
        var provider = http.RequestServices.GetService<IWalkthroughFactsProvider>();
        if (provider is null) return Error(StatusCodes.Status503ServiceUnavailable, "unavailable");
        var facts = await provider.ReadAsync(person, http.RequestAborted);
        return Results.Text(JsonSerializer.Serialize(new FactsReply(facts.CompanyCreated, facts.FirstAgentName, facts.FirstTaskGiven, facts.FirstAgentId, facts.FirstChatId), WalkthroughServerJson.Default.FactsReply), "application/json");
    }

    private static async Task<IResult> GetAsync(HttpContext http)
    {
        var (person, refusal) = await CallerAsync(http);
        if (person is null) return refusal!;
        var store = http.RequestServices.GetService<IWalkthroughPrefsStore>();
        if (store is null) return Error(StatusCodes.Status503ServiceUnavailable, "unavailable");
        var prefs = await store.GetAsync(person, http.RequestAborted);
        if (prefs is null) return Error(StatusCodes.Status404NotFound, "not_found");
        return Results.Text(JsonSerializer.Serialize(new PrefsBody(prefs.Dismissed, prefs.Finished, prefs.Current, [.. prefs.Completed]), WalkthroughServerJson.Default.PrefsBody), "application/json");
    }

    private static async Task<IResult> PutAsync(HttpContext http)
    {
        var (person, refusal) = await CallerAsync(http);
        if (person is null) return refusal!;
        var store = http.RequestServices.GetService<IWalkthroughPrefsStore>();
        if (store is null) return Error(StatusCodes.Status503ServiceUnavailable, "unavailable");

        PrefsBody? body;
        try
        {
            if (http.Request.ContentLength is > _maxBodyBytes) return Error(StatusCodes.Status413PayloadTooLarge, "too_large");
            using var buffer = new MemoryStream();
            await http.Request.Body.CopyToAsync(buffer, http.RequestAborted);
            if (buffer.Length > _maxBodyBytes) return Error(StatusCodes.Status413PayloadTooLarge, "too_large");
            body = JsonSerializer.Deserialize(buffer.ToArray(), WalkthroughServerJson.Default.PrefsBody);
        }
        catch (JsonException)
        {
            return Error(StatusCodes.Status400BadRequest, "invalid_body");
        }

        // Unknown step names are refused so the stored value always reads back (doc 2: prefs.set is validated by the server).
        if (body is null || body.Current is null || !_steps.Contains(body.Current) || body.Completed is null || body.Completed.Any(name => !_steps.Contains(name)))
        {
            return Error(StatusCodes.Status400BadRequest, "invalid_body");
        }

        await store.SetAsync(person, new WalkthroughPrefs(body.Dismissed, body.Finished, body.Current, [.. body.Completed.Distinct(StringComparer.Ordinal)]), http.RequestAborted);
        return Results.NoContent();
    }

    private static async Task<(string? Person, IResult? Refusal)> CallerAsync(HttpContext http)
    {
        var resolver = http.RequestServices.GetService<IWalkthroughCallerResolver>();
        if (resolver is null) return (null, Error(StatusCodes.Status503ServiceUnavailable, "unavailable"));
        var person = await resolver.ResolveAsync(http.Request.Headers.Authorization.ToString(), http.RequestAborted);
        return string.IsNullOrEmpty(person) ? (null, Error(StatusCodes.Status401Unauthorized, "unauthorized")) : (person, null);
    }

    private static IResult Error(int status, string code) =>
        Results.Text(JsonSerializer.Serialize(new ErrorBody(code), WalkthroughServerJson.Default.ErrorBody), "application/json", statusCode: status);
}
