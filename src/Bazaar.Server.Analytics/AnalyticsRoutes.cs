using Bazaar.Contracts.Panes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Bazaar.Server.Analytics;

/// <summary>
/// <c>GET /analytics?days=N</c> returns the analytics pane descriptor for the caller (default 28 days, 1 to 365). The host passes
/// <c>viewerOf</c>, which resolves the authenticated principal (null means 401), so this module never references Bazaar.Server.
/// </summary>
public static class AnalyticsRoutes
{
    private const int _defaultDays = 28;
    private const int _maxDays = 365;

    public static IEndpointRouteBuilder MapBazaarAnalytics(this IEndpointRouteBuilder app, Func<HttpContext, AnalyticsViewer?> viewerOf, TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(viewerOf);

        var time = clock ?? TimeProvider.System;
        app.MapGet("/analytics", async (HttpContext http, int? days, IAgentAnalytics analytics, CancellationToken ct) =>
        {
            if (viewerOf(http) is not { } viewer)
            {
                return Results.Unauthorized();
            }

            if (days is < 1 or > _maxDays)
            {
                return Results.BadRequest(new { error = "bad_days" });
            }

            var to = time.GetUtcNow();
            var window = new AnalyticsWindow(to - TimeSpan.FromDays(days ?? _defaultDays), to);
            var report = await analytics.ReadAsync(viewer, window, ct).ConfigureAwait(false);
            PaneDescriptor pane = AnalyticsPaneBuilder.Build(report);
            return Results.Json(new { kind = pane.Kind, title = pane.Title, capability = pane.Capability, model = pane.Model });
        });
        return app;
    }
}
