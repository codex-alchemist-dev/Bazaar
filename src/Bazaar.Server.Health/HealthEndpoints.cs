using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Bazaar.Server.Health;

/// <summary>
/// <c>GET /projects/{projectId}/health</c>: the project health metrics as JSON (doc 25). This is project health (CI, coverage,
/// debt, morale), not server health: <c>/health/live</c> and <c>/health/ready</c> stay with the status listener. Host composition
/// passes the HTTP surface guard (bearer, rate bucket) as <c>guard</c>, so this module never references Bazaar.Server.
/// Answers 200 with the report, or 400 <c>bad_project_id</c>.
/// </summary>
public static partial class HealthEndpoints
{
    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._-]{0,99}\z", RegexOptions.CultureInvariant)]
    private static partial Regex ProjectIdRegex();

    /// <summary>The same route behind a guard in the host's shape: a null result lets the request through, anything else answers it.</summary>
    public static IEndpointRouteBuilder MapBazaarProjectHealth(this IEndpointRouteBuilder app, Func<HttpContext, ValueTask<IResult?>> guard)
    {
        ArgumentNullException.ThrowIfNull(guard);
        return app.MapBazaarProjectHealth(builder => builder.AddEndpointFilter(async (context, next) =>
            await guard(context.HttpContext).ConfigureAwait(false) ?? await next(context).ConfigureAwait(false)));
    }

    public static IEndpointRouteBuilder MapBazaarProjectHealth(this IEndpointRouteBuilder app, Func<RouteHandlerBuilder, RouteHandlerBuilder> guard)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(guard);

        guard(app.MapGet("/projects/{projectId}/health",
            async (string projectId, [Microsoft.AspNetCore.Mvc.FromServices] IProjectHealthService health, CancellationToken ct) =>
            {
                if (!ProjectIdRegex().IsMatch(projectId))
                    return Results.Json(new { error = "bad_project_id" }, statusCode: StatusCodes.Status400BadRequest);

                var report = await health.GetAsync(projectId, ct);
                return Results.Text(
                    System.Text.Json.JsonSerializer.Serialize(report, HealthJsonContext.Default.ProjectHealthReport),
                    "application/json");
            }));

        return app;
    }
}
