using Bazaar.Core;
<<<<<<<< HEAD:src/Bazaar.Server.Org/OrgComponent.cs
using Bazaar.Server.Org.Data;
>>>>>>>> origin/main:src/Bazaar.Server.Calendar/CalendarComponent.cs
=======
using Bazaar.RyuseiFuda.Kanban;
using Bazaar.Server.Agents.Data;
using Bazaar.Server.Org.Data;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

<<<<<<<< HEAD:src/Bazaar.Server.Org/OrgComponent.cs
namespace Bazaar.Server.Org;

/// <summary>
/// The Company Org Chart module (docs/design/24 section 2). Registers the relation repository and the loan service, which
/// depend on the shared database and the GC components. Host composition maps <c>POST /company/org</c> with
/// <see cref="OrgReadRoute.MapCompanyOrg"/>.
/// </summary>
public sealed class OrgComponent : IBazaarComponent
{
    public static ComponentManifest ManifestDefinition { get; } = new(
        Id: "org",
        Version: "0.1.0",
        Kind: ComponentKind.Feature,
        DependsOn: ["gc"],
        Provides: ["feature:company-org"]);
namespace Bazaar.Server.Calendar;

/// <summary>
/// The Company Calendar module (docs/design/24 section 4). Registers the repository and the writer, which depend on the shared
/// database and the GC components. Host composition maps <c>POST /company/calendar</c> with
/// <see cref="CalendarReadRoute.MapCompanyCalendar"/>.
/// </summary>
public sealed class CalendarComponent : IBazaarComponent
{
    public static ComponentManifest ManifestDefinition { get; } = new(
        Id: "calendar",
        Version: "0.1.0",
        Kind: ComponentKind.Feature,
        DependsOn: ["gc"],
        Provides: ["feature:company-calendar"]);
>>>>>>>> origin/main:src/Bazaar.Server.Calendar/CalendarComponent.cs
=======

    public ComponentManifest Manifest => ManifestDefinition;

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

<<<<<<<< HEAD:src/Bazaar.Server.Org/OrgComponent.cs
        services.TryAddSingleton<OrgRelationRepository>();
        services.TryAddSingleton<IOrgRelationReader>(sp => sp.GetRequiredService<OrgRelationRepository>());
        services.TryAddSingleton<OrgLoanService>();
        services.TryAddSingleton<CalendarRepository>();
        services.TryAddSingleton<ICalendarService, CalendarService>();
>>>>>>>> origin/main:src/Bazaar.Server.Calendar/CalendarComponent.cs
=======
        services.TryAddSingleton<OrgRelationRepository>();
        services.TryAddSingleton<IOrgRelationReader>(sp => sp.GetRequiredService<OrgRelationRepository>());
        services.TryAddSingleton<OrgLoanService>();
        services.TryAddSingleton<IOrgDirectory>(sp =>
            new AgentOrgDirectory(sp.GetRequiredService<IAgentDirectoryReader>(), sp.GetService<IBoardDirectoryReader>()));
    }
}
