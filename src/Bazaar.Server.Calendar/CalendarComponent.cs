using Cdax.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Bazaar.Server.Calendar;

/// <summary>
/// The Company Calendar module (docs/design/24 section 4). Registers the repository and the writer, which depend on the shared
/// database and the GC components. Host composition maps <c>POST /company/calendar</c> with
/// <see cref="CalendarReadRoute.MapCompanyCalendar"/>.
/// </summary>
public sealed class CalendarComponent : ICdaxModule
{
    public static ComponentManifest ManifestDefinition { get; } = new(
        Id: "calendar",
        Version: "0.1.0",
        Kind: ComponentKind.Feature,
        DependsOn: ["gc"],
        Provides: ["feature:company-calendar"]);

    public ComponentManifest Manifest => ManifestDefinition;

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.TryAddSingleton<CalendarRepository>();
        services.TryAddSingleton<ICalendarService, CalendarService>();
    }
}
