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

    /// <summary>The authenticated API routes that also take writes: blob uploads and the Constellation tile endpoint.</summary>
    private static bool IsApiRoute(PathString path) =>
        path.StartsWithSegments("/blobs") || path.Equals("/constellation/tiles", StringComparison.Ordinal);

    /// <summary>
    /// The pairing routes (<c>/pair/*</c>, doc 32 section 3) and the refresh route (<c>/auth/refresh</c>, section 5). Each takes POST and
    /// nothing else; any other method on these paths is a 405.
    /// </summary>
    private static bool IsPostOnlyRoute(PathString path) =>
        path.Value is { } value
        && (value.StartsWith("/pair/", StringComparison.OrdinalIgnoreCase)
            || path.Equals("/auth/refresh", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Rejects every method the listener does not serve with 405 before routing. The status page is GET and HEAD only. The blob and
    /// Constellation API routes also take POST and PATCH. The pairing and refresh routes take POST only. No other write route is allowed here.
    /// </summary>
    public static IApplicationBuilder UseBazaarGetOnly(this IApplicationBuilder app) => app.Use(async (context, next) =>
    {
        var path = context.Request.Path;
        var method = context.Request.Method;
        bool allowed;
        string allow;
        if (IsPostOnlyRoute(path))
        {
            allowed = HttpMethods.IsPost(method);
            allow = "POST";
        }
        else
        {
            var isApi = IsApiRoute(path);
            allowed = HttpMethods.IsGet(method) || HttpMethods.IsHead(method) || (isApi && (HttpMethods.IsPost(method) || HttpMethods.IsPatch(method)));
            allow = isApi ? "GET, HEAD, POST, PATCH" : "GET, HEAD";
        }

        if (!allowed)
        {
            context.Response.Headers.Allow = allow;
            context.Response.StatusCode = StatusCodes.Status405MethodNotAllowed;
            return;
        }

        await next();
    });
}
