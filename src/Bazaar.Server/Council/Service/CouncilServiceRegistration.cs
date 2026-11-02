using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Bazaar.Server.Council.Service;

/// <summary>Dependency injection registration for the Council service.</summary>
public static class CouncilServiceRegistration
{
    /// <summary>
    /// Registers <see cref="CouncilService"/> as <see cref="ICouncilService"/>, with default <see cref="CouncilOptions"/>
    /// and <see cref="TimeProvider.System"/> unless the host registered them first. The host must also register
    /// <see cref="ICouncilStore"/>, <see cref="ICouncilGcEvents"/> and <see cref="IGcMembership"/>.
    /// </summary>
    // TODO(component): called only by host composition
    public static IServiceCollection AddBazaarCouncilService(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton(new CouncilOptions());
        services.TryAddSingleton<ICouncilService, CouncilService>();
        return services;
    }
}
