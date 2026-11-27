using Cdax.Core;
using Bazaar.Install.Abstractions;
using Bazaar.Install.Trust;
using Bazaar.ModulePermissions;
using Bazaar.ModulePermissions.Audit;
using Bazaar.Server.ModuleHost;
using Bazaar.Server.Modules.Persistence;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Bazaar.Server.Host.Composition;

public static class ModuleHostCompositionExtensions
{
    /// <summary>
    /// Wires the module host (doc 48 section 1): the permission gate with its audited denial sink, the module manager's Postgres
    /// approval store, the package signature verifier, and the activation host with its health poller. Call after
    /// <c>AddCdaxModules</c>, so the activation service starts after the component graph, and after the shared
    /// <c>NpgsqlDataSource</c> and the audit log are registered.
    /// <para>
    /// The trust store starts empty and the owner approval verifier refuses everything, because neither the publisher key
    /// registry nor the owner approval key (doc 32 section 5a, PROPOSED) exists yet. Both are <c>TryAdd</c>, so the real ones replace
    /// them. There is no package source either, so today every module is reported not packaged and nothing is in force.
    /// </para>
    /// </summary>
    public static IServiceCollection AddBazaarModuleHostWiring(this IServiceCollection services, IEnumerable<ComponentManifest> manifests)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(manifests);

        services.AddBazaarModulePermissionAudit();
        services.AddBazaarModulePermissions();
        services.TryAddSingleton<IModuleInstallStore, ModuleManagerRepository>();
        services.TryAddSingleton<ITrustedKeyStore>(new InMemoryTrustedKeyStore());
        services.TryAddSingleton<IModuleSignatureVerifier, EcdsaModuleSignatureVerifier>();
        services.TryAddSingleton<IOwnerApprovalVerifier, DenyAllOwnerApprovalVerifier>();
        return services.AddBazaarModuleHost(manifests);
    }
}
