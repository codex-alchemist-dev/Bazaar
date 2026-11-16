using Bazaar.Server.Modules.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Bazaar.Server.Modules.Install;

/// <summary>
/// Registration for the module install gate. A sibling of <see cref="ModuleManagerComponent"/>, not part of it: that component's
/// registrations are pure, and the gate needs host-supplied trust. The host must register <c>NpgsqlDataSource</c>,
/// <c>IAuditLog</c> and <c>ITransactionalAuditLog</c>, <c>IModuleSignatureVerifier</c> (with a real trusted key store) and
/// <c>IOwnerApprovalSigner</c>. This method registers none of them, so no placeholder key store or signer can be used by accident.
/// Resolving the services fails until the host supplies them.
/// </summary>
public static class ModuleInstallServiceCollectionExtensions
{
    /// <summary>Adds the repository, <see cref="ModuleInstallService"/> and <see cref="ModuleApprovalService"/> as singletons.</summary>
    public static IServiceCollection AddModuleInstallGate(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<ModuleManagerRepository>();
        services.AddSingleton<ModuleInstallService>();
        services.AddSingleton<ModuleApprovalService>();
        return services;
    }
}
