using Bazaar.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Bazaar.Server.TimeMachine;

/// <summary>
/// Time Machine as a Bazaar Component (docs/design/26-fun-systems.md, section 4). Registers the replay projector, the fork service
/// and the Postgres fork-link store. It depends on no component: the GC read seams (<c>IGcLogReader</c>, <c>IGcReadAccess</c>)
/// and <see cref="IForkTargetProvisioner"/> come from the GC module, which is a soft dependency so it starts first when enabled.
/// </summary>
public sealed class TimeMachineComponent : IBazaarComponent
{
    public static ComponentManifest ManifestDefinition { get; } = new(
        Id: "time-machine",
        Version: "0.1.0",
        Kind: ComponentKind.Feature,
        DependsOn: [],
        Provides: ["feature:time-machine"],
        SoftDependsOn: ["gc", "kanban", "vault"]);

    public ComponentManifest Manifest => ManifestDefinition;

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddBazaarTimeMachine();
    }
}

/// <summary>Registration for the Time Machine module.</summary>
public static class TimeMachineRegistration
{
    /// <summary>
    /// Registers the projector, the fork service and the Postgres link store. The host must register an <c>NpgsqlDataSource</c>,
    /// the GC read services and an <see cref="IForkTargetProvisioner"/>. Replay frame sources are optional: register them from
    /// the modules that own them, and a missing one shows as an unavailable part.
    /// </summary>
    public static IServiceCollection AddBazaarTimeMachine(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<IForkLinkStore, PostgresForkLinkStore>();
        services.TryAddSingleton<IReplayProjector>(sp => new ReplayProjector(
            sp.GetRequiredService<Bazaar.Server.Gc.Read.IGcLogReader>(),
            sp.GetRequiredService<Bazaar.Server.Gc.Read.IGcReadAccess>(),
            sp.GetService<IKanbanReplaySource>(),
            sp.GetService<IVaultReplaySource>(),
            sp.GetService<ISoulReplaySource>(),
            sp.GetService<IReputationReplaySource>()));
        services.TryAddSingleton<IReplayPointLister>(sp => new ReplayPointLister(
            sp.GetRequiredService<Bazaar.Server.Gc.Read.IGcLogReader>(),
            sp.GetRequiredService<Bazaar.Server.Gc.Read.IGcReadAccess>(),
            sp.GetService<IReplayMarkerSource>()));
        services.TryAddSingleton<IReplayMarkerSource, PostgresReplayMarkerSource>();
        services.TryAddSingleton<IForkService>(sp => new ForkService(
            sp.GetRequiredService<Bazaar.Server.Gc.Read.IGcReadAccess>(),
            sp.GetRequiredService<Bazaar.Server.Gc.Read.IGcLogReader>(),
            sp.GetRequiredService<IForkLinkStore>(),
            sp.GetRequiredService<IForkTargetProvisioner>(),
            sp.GetService<TimeProvider>()));
        return services;
    }
}
