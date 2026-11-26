using Bazaar.OfflineSync;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Bazaar.Server.OfflineSync;

/// <summary>Registration for the offline sync server module.</summary>
public static class OfflineSyncModule
{
    /// <summary>The component id of this module.</summary>
    public const string ComponentId = "offline-sync";

    /// <summary>
    /// Registers the batch processor and default store, audit and clock (each only if not already registered).
    /// Merge rules are taken from every registered <see cref="IOfflineMergeRule"/>.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The same collection.</returns>
    public static IServiceCollection AddBazaarOfflineSync(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<OfflineSyncOptions>();
        services.TryAddSingleton<IOfflineSyncStore, InMemoryOfflineSyncStore>();
        services.TryAddSingleton<IOfflineMergeAudit, NullOfflineMergeAudit>();
        services.TryAddSingleton<IOfflineClock, SystemOfflineClock>();
        services.TryAddSingleton<IOfflineWriteScopeFactory, NoOpOfflineWriteScopeFactory>();
        services.TryAddSingleton<OfflineSyncProcessor>(static sp => new OfflineSyncProcessor(
            sp.GetServices<IOfflineMergeRule>(),
            sp.GetRequiredService<IOfflineSyncStore>(),
            sp.GetRequiredService<IOfflineMergeAudit>(),
            sp.GetRequiredService<IOfflineClock>(),
            sp.GetRequiredService<OfflineSyncOptions>(),
            sp.GetRequiredService<IOfflineWriteScopeFactory>()));
        return services;
    }
}
