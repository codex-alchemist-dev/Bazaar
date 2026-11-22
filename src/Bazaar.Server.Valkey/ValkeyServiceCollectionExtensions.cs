using Bazaar.Server.Providers;
using Bazaar.Server.Secrets;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Bazaar.Server.Valkey;

/// <summary>Registration for the Valkey module. Only the component and the host composition root call it.</summary>
public static class ValkeyServiceCollectionExtensions
{
    /// <summary>
    /// Registers the Valkey cache provider, its client factory, the managed-child supervisor, the stale-child sweep and the health probes.
    /// Linux only: on every other OS nothing is registered, so no <see cref="ICacheProvider"/> reaches the Windows host (Garnet waits for
    /// spike S2, doc 03).
    /// </summary>
    public static IServiceCollection AddBazaarValkey(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        if (!OperatingSystem.IsLinux())
        {
            return services;
        }

        services.AddOptions<ValkeyCacheOptions>().Bind(configuration.GetSection(ValkeyCacheOptions.SectionName));
        services.AddOptions<ValkeySupervisionOptions>().Bind(configuration.GetSection(ValkeySupervisionOptions.SectionName));

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IValkeyClientFactory, StackExchangeValkeyClientFactory>();
        services.TryAddSingleton<IValkeyProcessLauncher, ValkeyProcessLauncher>();
        services.TryAddSingleton<IValkeyProcessProbe, LinuxValkeyProcessProbe>();
        services.TryAddSingleton<ValkeyStaleChildSweeper>();
        services.TryAddSingleton<ValkeyManagedConnection>();
        services.TryAddSingleton(sp => new ValkeyServerSupervisor(
            CacheOptions(sp),
            sp.GetRequiredService<IOptions<ValkeySupervisionOptions>>().Value,
            sp.GetRequiredService<IValkeyProcessLauncher>(),
            sp.GetRequiredService<IValkeyClientFactory>(),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<ILogger<ValkeyServerSupervisor>>(),
            sp.GetRequiredService<ValkeyManagedConnection>(),
            sp.GetRequiredService<ValkeyStaleChildSweeper>()));
        services.TryAddSingleton<IValkeyChildStatus>(sp => sp.GetRequiredService<ValkeyServerSupervisor>());
        services.AddHostedService(sp => sp.GetRequiredService<ValkeyServerSupervisor>());

        // One connection source for both the cache and the probes: the managed child's, or the operator's endpoint.
        services.TryAddSingleton<IValkeyConnectionSource>(sp => CacheOptions(sp).Managed
            ? sp.GetRequiredService<ValkeyManagedConnection>()
            : new ValkeyConfiguredEndpoint(CacheOptions(sp), sp.GetService<ISecretResolver>()));

        services.TryAddSingleton<IValkeyHealthProbes>(sp => new ValkeyHealthProbes(
            sp.GetRequiredService<IValkeyChildStatus>(),
            sp.GetRequiredService<IValkeyConnectionSource>(),
            sp.GetRequiredService<IValkeyClientFactory>(),
            CacheOptions(sp)));

        // Not TryAdd: several cache providers may be registered, and each consuming module decides what to do with several.
        services.AddSingleton<ICacheProvider>(sp =>
        {
            var options = CacheOptions(sp);
            return new ValkeyCacheProvider(
                options,
                sp.GetService<ISecretResolver>(),
                sp.GetRequiredService<IValkeyClientFactory>(),
                options.Managed ? sp.GetRequiredService<ValkeyManagedConnection>() : null);
        });

        return services;
    }

    private static ValkeyCacheOptions CacheOptions(IServiceProvider sp) =>
        sp.GetRequiredService<IOptions<ValkeyCacheOptions>>().Value;
}
