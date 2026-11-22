using Bazaar.Server.Agents.Data;
using Bazaar.Server.Calendar;
using Bazaar.Server.Http;
using Bazaar.Server.Interrupts;
using Bazaar.Server.Org;
using Bazaar.Server.Settings;
using Bazaar.Server.Wire.Connection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Bazaar.Server.Host.Http;

/// <summary>
/// Maps the Company pane read routes (doc 24 section 5) behind the HTTP surface guard (bearer, rate bucket).
/// The Org directory lists agent names from the Agents module; no project registry exists yet, so no project names are listed.
/// </summary>
internal static class CompanyReadRoutes
{
    private const string _timezoneItem = "bazaar.calendar.display_timezone";
    private const string _viewerItem = "bazaar.calendar.viewer";
    private const string _interruptViewerItem = "bazaar.interrupts.viewer";

    public static IEndpointRouteBuilder MapBazaarCompanyReadRoutes(this IEndpointRouteBuilder routes)
    {
        routes.MapCompanyOrg(HttpSurfaceEndpoints.GuardAsync);
        routes.MapCompanyCalendar(GuardCalendarAsync);
        routes.MapCompanyInterrupts(GuardInterruptsAsync);
        // Owner producer: the guard registers the viewer, and the module refuses anyone who is not the company owner with 403.
        routes.MapCompanyInterruptsWrite(GuardInterruptsAsync);
        return routes;
    }

    /// <summary>Registers the per-request calendar options; the setting is read after the guard passes, so a refusal costs nothing.</summary>
    public static IServiceCollection AddBazaarCompanyReadOptions(this IServiceCollection services)
    {
        services.AddHttpContextAccessor();
        services.TryAddSingleton<IAgentDirectoryReader>(sp => sp.GetRequiredService<AgentRepository>());
        services.AddScoped(sp =>
            sp.GetRequiredService<IHttpContextAccessor>().HttpContext?.Items[_timezoneItem] as CalendarReadOptions ?? new CalendarReadOptions());
        services.AddScoped(sp =>
            sp.GetRequiredService<IHttpContextAccessor>().HttpContext?.Items[_viewerItem] as CalendarViewer ?? CalendarViewer.Anonymous);
        services.AddScoped(sp =>
            sp.GetRequiredService<IHttpContextAccessor>().HttpContext?.Items[_interruptViewerItem] as InterruptViewer ?? InterruptViewer.Anonymous);
        return services;
    }

    private static async ValueTask<IResult?> GuardInterruptsAsync(HttpContext http)
    {
        var refused = await HttpSurfaceEndpoints.GuardAsync(http);
        if (refused is not null)
        {
            return refused;
        }

        var principal = await PrincipalAsync(http);
        http.Items[_interruptViewerItem] = principal is { } p ? new InterruptViewer(p.Id, p.Kind == "owner") : InterruptViewer.Anonymous;
        return null;
    }

    private static async ValueTask<IResult?> GuardCalendarAsync(HttpContext http)
    {
        var refused = await HttpSurfaceEndpoints.GuardAsync(http);
        if (refused is not null)
        {
            return refused;
        }

        var mode = await http.RequestServices.GetRequiredService<ISettingStore>()
            .GetAsync(CalendarSettings.DisplayTimezoneKey, cancellationToken: http.RequestAborted);
        http.Items[_timezoneItem] = new CalendarReadOptions(mode);
        http.Items[_viewerItem] = await ViewerAsync(http);
        return null;
    }

    /// <summary>
    /// The calendar viewer for a request that passed the guard. The guard does not hand the principal over, so the bearer is
    /// resolved again here; anything that does not resolve is the anonymous viewer, which sees public titles only.
    /// </summary>
    private static async Task<CalendarViewer> ViewerAsync(HttpContext http) =>
        await PrincipalAsync(http) is { } principal ? new CalendarViewer(principal.Id, principal.Kind == "owner") : CalendarViewer.Anonymous;

    private static async Task<PrincipalRef?> PrincipalAsync(HttpContext http)
    {
        var header = http.Request.Headers.Authorization.ToString();
        var token = header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? header[7..].Trim() : null;
        if (string.IsNullOrEmpty(token))
            return null;

        var auth = await http.RequestServices.GetRequiredService<IConnectionAuthenticator>()
            .AuthenticateAsync(new HelloCredential(token), new DeviceInfo("http", "http", string.Empty), http.RequestAborted);
        return auth is { Ok: true, Principal: { } principal } ? principal : null;
    }
}
