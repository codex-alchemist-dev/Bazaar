using Bazaar.Approvals;
using Bazaar.Roles.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace Bazaar.Roles;

public static class RolesRegistration
{
    /// <summary>
    /// Registers the Postgres store (on the shared <see cref="NpgsqlDataSource"/>, resolved lazily), the service, the checker and
    /// the owner-signed widening gate when an <see cref="IOwnerSignatureVerifier"/> is registered, and the fail-closed deny-all gate when not.
    /// </summary>
    public static IServiceCollection AddBazaarRoles(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IRoleStore>(sp => new PostgresRoleStore(sp.GetRequiredService<NpgsqlDataSource>()));
        services.TryAddSingleton<IGrantWideningGate>(sp =>
            sp.GetService<IOwnerSignatureVerifier>() is { } verifier
                ? new OwnerSignedWideningGate(verifier, sp.GetRequiredService<TimeProvider>(), sp.GetServices<IWideningBiometricCheck>())
                : new DenyAllWideningGate());
        services.TryAddSingleton<IPermissionChecker, PermissionChecker>();
        services.TryAddSingleton<IRoleService, RoleService>();
        return services;
    }
}
