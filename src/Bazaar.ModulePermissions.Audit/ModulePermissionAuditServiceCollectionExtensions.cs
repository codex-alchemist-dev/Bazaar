using Bazaar.Server.Security.Audit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Bazaar.ModulePermissions.Audit;

public static class ModulePermissionAuditServiceCollectionExtensions
{
    /// <summary>
    /// Registers the audit chain as the sink for refused module accesses. The host must have registered an
    /// <see cref="IAuditLog"/>.
    /// </summary>
    public static IServiceCollection AddBazaarModulePermissionAudit(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IModulePermissionDenialSink>(sp =>
            new AuditLogModulePermissionDenialSink(sp.GetRequiredService<IAuditLog>(), sp.GetRequiredService<TimeProvider>()));
        return services;
    }
}
