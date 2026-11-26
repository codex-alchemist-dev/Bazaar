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

    /// <summary>The authenticated API routes that also take writes: blob uploads, the Constellation tile endpoint and recording a Company Memory decision, and community draft decisions, and the Genetic Memory review routes.</summary>
    private static bool IsApiRoute(PathString path) =>
        path.StartsWithSegments("/blobs") || path.Equals("/constellation/tiles", StringComparison.Ordinal) || path.StartsWithSegments("/talent");
        path.StartsWithSegments("/blobs")
        || path.Equals("/constellation/tiles", StringComparison.Ordinal)
        || path.Equals("/company-memory/decisions", StringComparison.Ordinal)
        || path.Equals("/company-memory/pages", StringComparison.Ordinal)
        || path.StartsWithSegments("/community/drafts")
        || path.StartsWithSegments("/gmt")
        || path.Equals("/offline-sync/batch", StringComparison.Ordinal);

    /// <summary>The provisioning API (doc 45 section 1.9): <c>POST /setup/plans</c>, <c>POST /setup/plans/{hash}/apply</c>, <c>GET /setup/status</c>. Each route runs its own access gate.</summary>
    private static bool IsSetupRoute(PathString path) => path.StartsWithSegments("/setup");

    /// <summary>The provisioning API (doc 45 section 1.9): <c>POST /setup/plans</c>, <c>POST /setup/plans/{hash}/apply</c>, <c>GET /setup/status</c>. Each route runs its own access gate.</summary>
    private static bool IsSetupRoute(PathString path) => path.StartsWithSegments("/setup");

    /// <summary>
    /// The refresh, sign-out and handoff routes (<c>/auth/refresh</c>, <c>/auth/signout</c> and <c>/auth/handoff</c>, doc 32 sections 4 and 5). They take POST and
    /// nothing else; any other method on these paths is a 405.
    /// </summary>
    private static bool IsPostOnlyRoute(PathString path) =>
        path.Value is { } value
        && (path.Equals("/auth/refresh", StringComparison.OrdinalIgnoreCase)
            || path.Equals("/auth/signout", StringComparison.OrdinalIgnoreCase)
            || path.Equals("/auth/handoff", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The walkthrough preference route (<c>/prefs/walkthrough</c>, doc 45 section 2.9, PROPOSED). It takes GET and PUT and nothing else.
    /// </summary>
    private static bool IsPrefsRoute(PathString path) => path.Equals("/prefs/walkthrough", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Rejects every method the listener does not serve with 405 before routing. The status page is GET and HEAD only. The blob and
    /// Constellation API routes also take POST and PATCH. The refresh route takes POST only. No other write route is allowed here.
    /// </summary>
    public static IApplicationBuilder UseBazaarGetOnly(this IApplicationBuilder app) => app.Use(async (context, next) =>
    {
        var path = context.Request.Path;
        var method = context.Request.Method;
        if (path.StartsWithSegments("/admin") || path.StartsWithSegments("/signin"))
        {
            // The admin panel (doc 51) and the sign-in page (doc 32 section 4) enforce their own method tables and answer 405 themselves.
            await next();
            return;
        }

        bool allowed;
        string allow;
        if (IsPostOnlyRoute(path))
        {
            allowed = HttpMethods.IsPost(method);
            allow = "POST";
        }
        else if (IsPrefsRoute(path))
        {
            allowed = HttpMethods.IsGet(method) || HttpMethods.IsHead(method) || HttpMethods.IsPut(method);
            allow = "GET, HEAD, PUT";
        }
        else
        {
            var isApi = IsApiRoute(path) || IsSetupRoute(path);
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
