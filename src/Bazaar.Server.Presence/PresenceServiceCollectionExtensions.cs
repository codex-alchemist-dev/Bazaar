using Bazaar.Server.Presence.Abstractions;
using Bazaar.Server.Providers;
using Bazaar.Server.Wire.Connection;
using Bazaar.Server.Wire.Sync;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace Bazaar.Server.Presence;

/// <summary>Registers the presence module (docs/design/23, "Activity and presence").</summary>
public static class PresenceServiceCollectionExtensions
{
    /// <summary>
    /// Adds the projector, the store, the publisher, the <c>presence.snapshot</c> handler and the rebuild and sweep service. The
    /// store is in memory; when an <see cref="ICacheProvider"/> is registered, rows are also written to it as a snapshot cache.
    /// Options bind from <c>Bazaar:Presence</c>.
    /// </summary>
    public static IServiceCollection AddBazaarPresence(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.TryAddSingleton(PresenceOptionsReader.Read(configuration.GetSection(PresenceOptions.SectionName)));
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IPresenceStore>(sp =>
        {
            // A registered durable store (Postgres agent_activity) is the truth; otherwise the in-memory store works until restart.
            IPresenceStore inner = (IPresenceStore?)sp.GetService<IDurablePresenceStore>() ?? new InMemoryPresenceStore();
            return sp.GetService<ICacheProvider>() is { } cache
                ? new CacheSnapshotPresenceStore(inner, cache, sp.GetRequiredService<PresenceOptions>())
                : inner;
        });
        services.AddSingleton<IStreamGate, PresenceStreamGate>();
        services.TryAddSingleton<IPresencePublisher>(sp => new PresencePublisher(
            sp.GetRequiredService<IPresenceStore>(),
            sp.GetService<IWireConnectionDirectory>(),
            sp.GetService<SyncOptions>() ?? new SyncOptions(),
            sp.GetRequiredService<TimeProvider>()));
        services.TryAddSingleton(sp => new ActivityProjector(
            sp.GetRequiredService<IPresenceStore>(),
            sp.GetRequiredService<IPresencePublisher>(),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<PresenceOptions>()));
        services.TryAddSingleton<IActivityProjector>(sp => sp.GetRequiredService<ActivityProjector>());
        services.AddSingleton<IWireRequestHandler, PresenceSnapshotHandler>();
        services.AddSingleton<IHostedService>(sp => new PresenceHostedService(
            sp.GetRequiredService<ActivityProjector>(),
            sp.GetRequiredService<PresenceOptions>(),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetService<IPresenceSeedSource>()));
        return services;
    }
}
