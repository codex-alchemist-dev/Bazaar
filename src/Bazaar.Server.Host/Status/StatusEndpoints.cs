using Bazaar.Contracts;
using Bazaar.Server.Host.Startup;
using Bazaar.Server.Providers;

namespace Bazaar.Server.Host.Status;

/// <summary>
/// The GET-only status listener (doc 03). <c>/health/live</c> says the process is up; <c>/health/ready</c> says every readiness check passes.
/// <c>/status</c> is a minimal version, uptime and provider report; the full format belongs to docs 02 and 23. There is no <c>/healthz</c>.
/// Output never carries chat, Vault text or secret values.
/// </summary>
public static class StatusEndpoints
{
    public static IEndpointRouteBuilder MapBazaarStatus(this IEndpointRouteBuilder routes)
    {
        routes.MapGet("/health/live", () => Results.Text("live"));

        routes.MapGet("/health/ready", async (HostState state, IEnumerable<IReadinessCheck> checks, CancellationToken ct) =>
        {
            var results = new List<ReadinessResult> { new("startup", state.Started, state.Started ? null : "starting") };
            if (state.Started)
            {
                foreach (var check in checks)
                {
                    results.Add(await check.CheckAsync(ct));
                }
            }

            var ready = results.All(r => r.Ok);
            return Results.Json(new { ready, checks = results.Select(r => new { name = r.Name, ok = r.Ok, detail = r.Detail }) },
                statusCode: ready ? StatusCodes.Status200OK : StatusCodes.Status503ServiceUnavailable);
        });

        routes.MapGet("/status", (HostState state, ProviderCapabilityReport providers) => Results.Json(new
        {
            version = ProductVersion.Informational,
            codename = ProductVersion.CurrentCodename,
            started_at = state.StartedAt,
            uptime_seconds = (long)state.Uptime.TotalSeconds,
            providers = providers.Lines,
        }));

        return routes;
    }

    /// <summary>Rejects every method but GET and HEAD with 405 before routing, so no write route can exist on this listener.</summary>
    public static IApplicationBuilder UseBazaarGetOnly(this IApplicationBuilder app) => app.Use(async (context, next) =>
    {
        if (!HttpMethods.IsGet(context.Request.Method) && !HttpMethods.IsHead(context.Request.Method))
        {
            context.Response.Headers.Allow = "GET, HEAD";
            context.Response.StatusCode = StatusCodes.Status405MethodNotAllowed;
            return;
        }

        await next();
    });
}
