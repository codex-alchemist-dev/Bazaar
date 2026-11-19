using Bazaar.Core;
using Bazaar.Server.Modules.Releases;
using Bazaar.Server.Modules.Updates;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Bazaar.Server.Modules;

/// <summary>
/// The module manager's pure services as a Bazaar Component (docs/design/42-deps-as-modules.md, BAZAAR-COMPONENTS.md section 2).
/// Registers the update policy evaluator, the defer aging report and the release lifecycle. Registrations only: no I/O, no settings
/// read here. The clock is the kernel's <see cref="TimeProvider"/>, added only when nothing else registered one.
/// The module manager's SQL (the <c>modules</c> migration namespace) stays under <c>Data/Sql/modules</c> in the Bazaar.Server assembly,
/// because the migration source is that assembly only (the same rule <c>CommunityComponent</c> follows).
/// </summary>
/// <remarks>
/// <see cref="MaintenanceWindowValidator"/> and <see cref="RingHealthGate"/> are static, so there is nothing to register for them;
/// callers use them directly.
/// </remarks>
public sealed class ModuleManagerComponent : IBazaarComponent
{
    public static ComponentManifest ManifestDefinition { get; } = new(
        Id: "module-manager",
        Version: "0.1.0",
        Kind: ComponentKind.Feature,
        DependsOn: [],
        Provides: ["feature:module-manager"]);

    public ComponentManifest Manifest => ManifestDefinition;

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<UpdatePolicyEvaluator>();
        services.AddSingleton<DeferAging>();
        services.AddSingleton<ReleaseCandidateLifecycle>();
    }
}
