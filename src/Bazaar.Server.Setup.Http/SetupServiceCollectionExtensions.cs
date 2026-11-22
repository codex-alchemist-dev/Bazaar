using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Bazaar.Server.Setup.Http;

public static class SetupServiceCollectionExtensions
{
    /// <summary>
    /// Registers the provisioning API's own service: the apply coordinator. The gate, the stores, the progress source and the
    /// applier come from the caller, because their durable implementations are later slices. Until the caller registers them,
    /// <see cref="SetupEndpoints.MapSetupEndpoints"/> answers 503 and writes nothing.
    /// </summary>
    public static IServiceCollection AddSetupHttp(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<SetupApplyCoordinator>();
        return services;
    }
}
