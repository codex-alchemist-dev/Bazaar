using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Bazaar.Server.Install;

/// <summary>
/// Registration for the module install gate. The host must register <c>IModuleInstallStore</c>, <c>IModuleSignatureVerifier</c>
/// (with a real trusted key store) and <c>IOwnerApprovalSigner</c>. This method registers none of them, so no placeholder key
/// store, signer or store can be used by accident. Resolving the services fails until the host supplies them.
/// </summary>
public static class ModuleInstallServiceCollectionExtensions
{
    /// <summary>Adds <see cref="ModuleInstallService"/> and <see cref="ModuleApprovalService"/> as singletons.</summary>
    public static IServiceCollection AddModuleInstallGate(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<ModuleInstallService>();
        services.AddSingleton<ModuleApprovalService>();
        return services;
    }
}
