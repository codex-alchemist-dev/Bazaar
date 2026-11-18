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
    /// Registers the Valkey cache provider, its client factory and the managed-child supervisor. Linux only: on every other OS nothing is
    /// registered, so no <see cref="ICacheProvider"/> reaches the Windows host (Garnet waits for spike S2, doc 03).
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

        // A managed child is reached on loopback when the operator set no endpoint or secret of its own.
        services.PostConfigure<ValkeyCacheOptions>(options =>
        {
            if (options.Managed && string.IsNullOrWhiteSpace(options.Endpoint) && string.IsNullOrWhiteSpace(options.SecretReference))
            {
                options.Endpoint = $"127.0.0.1:{options.ManagedPort}";
            }
        });

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IValkeyClientFactory, StackExchangeValkeyClientFactory>();
        services.TryAddSingleton<IValkeyProcessLauncher, ValkeyProcessLauncher>();
        services.TryAddSingleton(sp => new ValkeyServerSupervisor(
            sp.GetRequiredService<IOptions<ValkeyCacheOptions>>().Value,
            sp.GetRequiredService<IOptions<ValkeySupervisionOptions>>().Value,
            sp.GetRequiredService<IValkeyProcessLauncher>(),
            sp.GetRequiredService<IValkeyClientFactory>(),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<ILogger<ValkeyServerSupervisor>>()));
        services.TryAddSingleton<IValkeyChildStatus>(sp => sp.GetRequiredService<ValkeyServerSupervisor>());
        services.AddHostedService(sp => sp.GetRequiredService<ValkeyServerSupervisor>());

        // Not TryAdd: several cache providers may be registered, and the host selects one through ProviderSelector.
        services.AddSingleton<ICacheProvider>(sp => new ValkeyCacheProvider(
            sp.GetRequiredService<IOptions<ValkeyCacheOptions>>().Value,
            sp.GetService<ISecretResolver>(),
            sp.GetRequiredService<IValkeyClientFactory>()));

        return services;
    }
}
