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

    /// <summary>Largest report body accepted, in characters. A SARIF log of a large solution stays well under it.</summary>
    public const int MaxReportCharacters = 8_000_000;

    /// <summary>
    /// <c>POST /projects/{projectId}/health/reports</c> (PROPOSED, doc 40): a CI run or an agent pushes one raw report
    /// (<c>coverage</c>, <c>analyzer</c> or <c>package_audit</c>). The body is <see cref="HealthReportRequest"/>; <c>day</c> is optional
    /// (default today, UTC). A second push with the same kind, toolchain, day and source reference replaces the first. The caller
    /// passes a guard that also checks who may push; this module never references Bazaar.Server. Answers 200 <c>stored</c>,
    /// 400 (<c>bad_project_id</c>, <c>bad_report</c>) or 413 <c>report_too_large</c>.
    /// </summary>
    public static IEndpointRouteBuilder MapBazaarProjectHealthReports(this IEndpointRouteBuilder app, Func<HttpContext, ValueTask<IResult?>> guard)
    {
        ArgumentNullException.ThrowIfNull(guard);
        return app.MapBazaarProjectHealthReports(builder => builder.AddEndpointFilter(async (context, next) =>
            await guard(context.HttpContext).ConfigureAwait(false) ?? await next(context).ConfigureAwait(false)));
    }

    public static IEndpointRouteBuilder MapBazaarProjectHealthReports(this IEndpointRouteBuilder app, Func<RouteHandlerBuilder, RouteHandlerBuilder> guard)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(guard);

        guard(app.MapPost("/projects/{projectId}/health/reports",
            async (string projectId, HttpContext http, [Microsoft.AspNetCore.Mvc.FromServices] IHealthReportStore store, [Microsoft.AspNetCore.Mvc.FromServices] TimeProvider time, CancellationToken ct) =>
            {
                if (!ProjectIdRegex().IsMatch(projectId))
                    return Results.Json(new { error = "bad_project_id" }, statusCode: StatusCodes.Status400BadRequest);

                HealthReportRequest? body;
                try
                {
                    body = await System.Text.Json.JsonSerializer.DeserializeAsync(http.Request.Body, HealthJsonContext.Default.HealthReportRequest, ct);
                }
                catch (System.Text.Json.JsonException)
                {
                    body = null;
                }

                if (body is null || !HealthReportKinds.All.Contains(body.Kind)
                    || string.IsNullOrWhiteSpace(body.Toolchain) || body.Toolchain.Length > 64
                    || string.IsNullOrWhiteSpace(body.SourceRef) || body.SourceRef.Length > 200
                    || string.IsNullOrWhiteSpace(body.Content))
                {
                    return Results.Json(new { error = "bad_report" }, statusCode: StatusCodes.Status400BadRequest);
                }

                if (body.Content.Length > MaxReportCharacters)
                    return Results.Json(new { error = "report_too_large" }, statusCode: StatusCodes.Status413PayloadTooLarge);

                DateOnly day;
                if (string.IsNullOrWhiteSpace(body.Day))
                    day = DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime);
                else if (!DateOnly.TryParseExact(body.Day, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out day))
                    return Results.Json(new { error = "bad_report" }, statusCode: StatusCodes.Status400BadRequest);

                await store.PutAsync(new HealthReport(projectId, body.Kind, body.Toolchain.Trim(), day, body.SourceRef.Trim(), body.Content), ct);
                return Results.Json(new { stored = true });
            }));

        return app;
    }
}

/// <summary>The body of <c>POST /projects/{projectId}/health/reports</c>.</summary>
public sealed record HealthReportRequest(string Kind, string Toolchain, string? Day, string SourceRef, string Content);
