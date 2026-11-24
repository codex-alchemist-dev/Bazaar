using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Bazaar.Server.SoulProfile;

/// <summary>
/// The read-only profile route (doc 07, PROPOSED): <c>GET /agents/{agent_id}/profile</c>. A signed-in person is required (401). An
/// unknown agent, an unbound one, a malformed id and a refused reader all answer the same 404. A route answers 503 until its ports
/// are registered. Transport rules and the rate bucket are applied by the host that maps the route.
/// </summary>
public static class SoulProfileEndpoints
{
    public static IEndpointRouteBuilder MapSoulProfileEndpoints(this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);
        routes.Map("/agents/{agentId}/profile", async http => await (await HandleAsync(http)).ExecuteAsync(http))
            .WithMetadata(new HttpMethodMetadata(["GET"]));
        return routes;
    }

    private static async Task<IResult> HandleAsync(HttpContext http)
    {
        var resolver = http.RequestServices.GetService<ISoulProfileCallerResolver>();
        var source = http.RequestServices.GetService<ISoulProfileSource>();
        if (resolver is null || source is null) return Error(StatusCodes.Status503ServiceUnavailable, "unavailable");

        var person = await resolver.ResolveAsync(http.Request.Headers.Authorization.ToString(), http.RequestAborted);
        if (string.IsNullOrEmpty(person)) return Error(StatusCodes.Status401Unauthorized, "unauthorized");

        if (!Guid.TryParse(http.Request.RouteValues["agentId"]?.ToString(), out var agentId)) return Error(StatusCodes.Status404NotFound, "not_found");
        var profile = await source.ReadAsync(person, agentId, http.RequestAborted);
        if (profile is null) return Error(StatusCodes.Status404NotFound, "not_found");

        var body = new ProfileBody(
            profile.SoulId, profile.Name, profile.Role, profile.ModelBadge,
            [.. profile.Versions.Select(v => new VersionBody(v.Version, v.ParentVersion, v.CharterSha256, v.PreferencesJson, v.ContentSha256, v.Author, v.Reason, v.DecisionRef, v.CreatedAt))]);
        return Results.Text(JsonSerializer.Serialize(body, SoulProfileJson.Default.ProfileBody), "application/json");
    }

    private static IResult Error(int status, string code) =>
        Results.Text(JsonSerializer.Serialize(new ErrorBody(code), SoulProfileJson.Default.ErrorBody), "application/json", statusCode: status);
}
