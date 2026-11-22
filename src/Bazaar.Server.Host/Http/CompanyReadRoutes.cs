using Bazaar.Server.Agents.Data;
using Bazaar.Server.Calendar;
using Bazaar.Server.Http;
using Bazaar.Server.Org;
using Bazaar.Server.Settings;
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

    public static IEndpointRouteBuilder MapBazaarCompanyReadRoutes(this IEndpointRouteBuilder routes)
    {
        routes.MapCompanyOrg(HttpSurfaceEndpoints.GuardAsync);
        routes.MapCompanyCalendar(GuardCalendarAsync);
        return routes;
    }

    /// <summary>Registers the per-request calendar options; the setting is read after the guard passes, so a refusal costs nothing.</summary>
    public static IServiceCollection AddBazaarCompanyReadOptions(this IServiceCollection services)
    {
        services.AddHttpContextAccessor();
        services.TryAddSingleton<IAgentDirectoryReader>(sp => sp.GetRequiredService<AgentRepository>());
        services.AddScoped(sp =>
            sp.GetRequiredService<IHttpContextAccessor>().HttpContext?.Items[_timezoneItem] as CalendarReadOptions ?? new CalendarReadOptions());
        return services;
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
        return null;
    }
}
