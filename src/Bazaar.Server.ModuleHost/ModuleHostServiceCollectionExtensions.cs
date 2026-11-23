using Bazaar.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace Bazaar.Server.ModuleHost;

public static class ModuleHostServiceCollectionExtensions
{
    /// <summary>
    /// Registers the activation host and its hosted service. The caller must also register <c>IModuleInstallStore</c>,
    /// <c>IModuleSignatureVerifier</c>, <c>IOwnerApprovalVerifier</c> and the permission gate (<c>AddBazaarModulePermissions</c>);
    /// there are no placeholders, so resolving fails until the host supplies them. Without an <see cref="IModulePackageSource"/>
    /// no module has a package and none is put in force. Call it after the component graph, so the hosted service starts after it.
    /// </summary>
    public static IServiceCollection AddBazaarModuleHost(this IServiceCollection services, IEnumerable<ComponentManifest> manifests)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(manifests);

        var list = manifests.ToList();
        services.TryAddSingleton<IModulePackageSource, NoModulePackages>();
        services.AddSingleton(sp => ActivatorUtilities.CreateInstance<ModuleActivationHost>(sp, (IEnumerable<ComponentManifest>)list));
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<IHostedService, ModuleActivationHostedService>();
        // Registered after the activation service, so the first poll can only deactivate sets that are already in force.
        services.AddSingleton<ModuleHealthPoller>();
        services.AddSingleton<IHostedService>(sp => sp.GetRequiredService<ModuleHealthPoller>());
        return services;
    }
}
