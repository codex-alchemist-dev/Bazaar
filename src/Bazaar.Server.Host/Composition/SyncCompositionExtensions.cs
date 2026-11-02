using Bazaar.Server.Wire.Sync;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Bazaar.Server.Host.Composition;

/// <summary>
/// Wire sync (doc 02): the subscription engine, the GC dispatcher and their settings. Registered with <c>TryAdd</c>, so a
/// host or test may replace any of them first.
/// </summary>
/// <remarks>
/// <para>
/// The caller must still supply these services, because no implementation exists in the tree yet:
/// <list type="bullet">
/// <item><see cref="IGcStreamSource"/> (the G3 Postgres read side). <see cref="SubscriptionEngine"/> and <see cref="GcDispatcher"/> need it.</item>
/// <item><see cref="IGcAccessPolicy"/> (membership and DM rules). <see cref="SubscriptionEngine"/> needs it.</item>
/// </list>
/// Until they are supplied, resolving the engine or dispatcher throws <see cref="InvalidOperationException"/>; the container itself still builds.
/// </para>
/// <para>
/// <see cref="GcDispatcher"/> is a singleton, not a hosted service: it has no <c>StartAsync</c>. Its <c>RunAsync</c> needs an
/// <c>IGcNotificationSource</c>, which no type in the tree implements yet, so the caller starts it.
/// <see cref="IConnectionOutbound"/> is per connection (the connection manager supplies it through
/// <see cref="SubscriptionEngine.CreateSession"/>) and is not registered here.
/// </para>
/// </remarks>
public static class SyncCompositionExtensions
{
    /// <summary>Binds <see cref="SyncOptions"/> from <c>Bazaar:Sync</c>, and registers the subscription engine and GC dispatcher.</summary>
    public static IServiceCollection AddBazaarSync(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<SyncOptions>().Bind(configuration.GetSection(_syncSection));
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton(sp => new GcDispatcher(
            sp.GetRequiredService<IGcStreamSource>(),
            sp.GetRequiredService<IOptions<SyncOptions>>().Value,
            sp.GetService<TimeProvider>()));
        services.TryAddSingleton(sp => new SubscriptionEngine(
            sp.GetRequiredService<IGcStreamSource>(),
            sp.GetRequiredService<IGcAccessPolicy>(),
            sp.GetRequiredService<GcDispatcher>(),
            sp.GetRequiredService<IOptions<SyncOptions>>().Value,
            sp.GetService<TimeProvider>()));
        return services;
    }

    private const string _syncSection = "Bazaar:Sync";
}
