using Bazaar.Server.Providers;
using Bazaar.Server.Secrets;
using Bazaar.Server.Valkey;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Bazaar.Server.Garnet;

/// <summary>Registration for the Garnet module. Only the component and the host composition root call it.</summary>
public static class GarnetServiceCollectionExtensions
{
    /// <summary>
    /// Registers the Garnet cache provider, its client factory, the managed-child supervisor, the stale-child sweep and the health probes.
    /// Windows only: on every other OS nothing is registered, so no <see cref="ICacheProvider"/> from this module reaches the host.
    /// </summary>
    public static IServiceCollection AddBazaarGarnet(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        if (!OperatingSystem.IsWindows())
        {
            return services;
        }

        services.AddOptions<GarnetCacheOptions>().Bind(configuration.GetSection(GarnetCacheOptions.SectionName));
        services.AddOptions<GarnetSupervisionOptions>().Bind(configuration.GetSection(GarnetSupervisionOptions.SectionName));

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IValkeyClientFactory, StackExchangeGarnetClientFactory>();
        services.TryAddSingleton<IGarnetProcessLauncher, GarnetProcessLauncher>();
        services.TryAddSingleton<IGarnetProcessProbe, ProcessGarnetProcessProbe>();
        services.TryAddSingleton<GarnetStaleChildSweeper>();
        services.TryAddSingleton<GarnetManagedConnection>();
        services.TryAddSingleton(sp => new GarnetServerSupervisor(
            CacheOptions(sp),
            sp.GetRequiredService<IOptions<GarnetSupervisionOptions>>().Value,
            sp.GetRequiredService<IGarnetProcessLauncher>(),
            sp.GetRequiredService<IValkeyClientFactory>(),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<ILogger<GarnetServerSupervisor>>(),
            sp.GetRequiredService<GarnetManagedConnection>(),
            sp.GetRequiredService<GarnetStaleChildSweeper>()));
        services.TryAddSingleton<IValkeyChildStatus>(sp => sp.GetRequiredService<GarnetServerSupervisor>());
        services.AddHostedService(sp => sp.GetRequiredService<GarnetServerSupervisor>());

        // One connection source for both the cache and the probes: the managed child's, or the operator's endpoint.
        services.TryAddSingleton<IValkeyConnectionSource>(sp => CacheOptions(sp).Managed
            ? sp.GetRequiredService<GarnetManagedConnection>()
            : new GarnetConfiguredEndpoint(CacheOptions(sp), sp.GetService<ISecretResolver>()));

        services.TryAddSingleton<IValkeyHealthProbes>(sp => new GarnetHealthProbes(
            sp.GetRequiredService<IValkeyChildStatus>(),
            sp.GetRequiredService<IValkeyConnectionSource>(),
            sp.GetRequiredService<IValkeyClientFactory>(),
            CacheOptions(sp)));

        // Not TryAdd: several cache providers may be registered, and the host selects one through ProviderSelector.
        services.AddSingleton<ICacheProvider>(sp =>
        {
            var options = CacheOptions(sp);
            return new GarnetCacheProvider(
                options,
                sp.GetService<ISecretResolver>(),
                sp.GetRequiredService<IValkeyClientFactory>(),
                options.Managed ? sp.GetRequiredService<GarnetManagedConnection>() : null);
        });

        return services;
    }

    private static GarnetCacheOptions CacheOptions(IServiceProvider sp) =>
        sp.GetRequiredService<IOptions<GarnetCacheOptions>>().Value;
}
