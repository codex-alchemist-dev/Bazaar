using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Bazaar.ModulePermissions;

public static class ModulePermissionServiceCollectionExtensions
{
    /// <summary>
    /// Registers the runtime permission gate as one singleton behind <see cref="IModulePermissionGate"/> and
    /// <see cref="IModulePermissionRegistry"/>. The host must register an <see cref="IModulePermissionDenialSink"/>: there is
    /// no default, because a gate whose refusals are not audited does not meet doc 48 section 1.
    /// </summary>
    public static IServiceCollection AddBazaarModulePermissions(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<ModulePermissionGate>();
        services.TryAddSingleton<IModulePermissionGate>(sp => sp.GetRequiredService<ModulePermissionGate>());
        services.TryAddSingleton<IModulePermissionRegistry>(sp => sp.GetRequiredService<ModulePermissionGate>());
        return services;
    }
}
