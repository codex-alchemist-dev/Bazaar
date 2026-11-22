using System.Text.Json;
using System.Text.RegularExpressions;
using Bazaar.Contracts.Wire;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Matching;
using Microsoft.Extensions.DependencyInjection;

namespace Bazaar.Server.GitSolarSystem;

/// <summary>
/// The route <c>GET /git/{provider}/repos/{owner}/{repo}/solar</c> (doc 19, Git Solar System). The body is a
/// <see cref="Bazaar.Contracts.Graph.GitSolarFeed"/> serialised through <see cref="WireJsonContext"/>.
/// </summary>
/// <remarks>
/// The guard runs first. A non-null guard result is returned as it is (the host passes its guarded-HTTP-surface check here).
/// An unknown repository answers 404. A provider with characters outside letters, digits, <c>-</c> and <c>_</c> answers 400.
/// A history over the commit cap answers 200 with the newest commits and <c>X-Truncated: true</c>.
/// </remarks>
public static partial class GitSolarRoutes
{
    /// <summary>Header set when the history was cut to the newest commits.</summary>
    public const string TruncatedHeader = "X-Truncated";

    [GeneratedRegex("^[A-Za-z0-9_-]{1,64}$", RegexOptions.CultureInvariant)]
    private static partial Regex ProviderPattern();

    /// <summary>Maps the solar route onto <paramref name="routes"/>.</summary>
    public static IEndpointRouteBuilder Map(
        this IEndpointRouteBuilder routes,
        Func<HttpContext, ValueTask<IResult?>> guard)
    {
        ArgumentNullException.ThrowIfNull(routes);
        ArgumentNullException.ThrowIfNull(guard);

        // A RequestDelegate with the GET method metadata keeps the route free of reflection, so the module stays AOT-safe.
        RequestDelegate handler = async http =>
        {
            var result = await SolarAsync(http, guard);
            await result.ExecuteAsync(http);
        };
        routes.Map("/git/{provider}/repos/{owner}/{repo}/solar", handler)
            .WithMetadata(new HttpMethodMetadata(["GET"]));

        return routes;
    }

    private static async Task<IResult> SolarAsync(HttpContext http, Func<HttpContext, ValueTask<IResult?>> guard)
    {
        var refused = await guard(http);
        if (refused is not null)
        {
            return refused;
        }

        var provider = http.Request.RouteValues["provider"]?.ToString() ?? string.Empty;
        var owner = http.Request.RouteValues["owner"]?.ToString() ?? string.Empty;
        var repo = http.Request.RouteValues["repo"]?.ToString() ?? string.Empty;
        if (!ProviderPattern().IsMatch(provider))
        {
            return Results.BadRequest();
        }

        var feeds = http.RequestServices.GetRequiredService<GitSolarFeedService>();
        var result = await feeds.GetFeedAsync(provider, owner, repo, http.RequestAborted);
        if (result is null)
        {
            return Results.NotFound();
        }

        if (result.Truncated)
        {
            http.Response.Headers[TruncatedHeader] = "true";
        }

        var json = JsonSerializer.Serialize(result.Feed, WireJsonContext.Default.GitSolarFeed);
        return Results.Text(json, "application/json");
    }
}
