using Bazaar.Server.Calendar;
using Bazaar.Server.Components;
using Bazaar.Server.Org.Data;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Bazaar.Server.Company;

/// <summary>
/// Company systems (docs/design/24-company-systems-core.md). Registers the Calendar and Org services that depend on the shared
/// database and GC components. Interrupt stack registration waits for the personal-data key provider integration.
/// </summary>
public sealed class CompanyComponent : IBazaarComponent
{
    public static ComponentManifest ManifestDefinition { get; } = new(
        Id: "company",
        Version: "0.1.0",
        Kind: ComponentKind.Feature,
        DependsOn: ["gc"],
        Provides: ["feature:company-calendar", "feature:company-org"]);

    public ComponentManifest Manifest => ManifestDefinition;

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.TryAddSingleton<CalendarRepository>();
        services.TryAddSingleton<ICalendarService, CalendarService>();
        services.TryAddSingleton<OrgRelationRepository>();
        services.TryAddSingleton<OrgLoanService>();
    }
}
