using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Bazaar.Server.AgentSafety.Drill;

/// <summary>
/// Registers the kill switch drill. The host calls this once it has a kill switch engine, containment, a probe and a drill target
/// (those are required and not defaulted). Every default is TryAdd, so a durable store registered before the call is kept.
/// </summary>
public static class DrillServiceCollectionExtensions
{
    /// <summary>Registers the runner, the result store (in-memory by default), the status port and the scheduler.</summary>
    public static IServiceCollection AddBazaarKillSwitchDrill(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddOptions<DrillOptions>();
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<DrillRunner>();
        services.TryAddSingleton<IDrillResultStore, InMemoryDrillResultStore>();
        services.TryAddSingleton<IDrillStatus, DrillStatus>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, DrillScheduler>());
        return services;
    }
}
