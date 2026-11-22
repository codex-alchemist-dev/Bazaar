using Bazaar.Server.Setup.FirstRun;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Bazaar.Server.Setup.FirstRun.Gateway;

/// <summary>Registers the gateway-facing side of the first-run seed. The host supplies the two directory ports.</summary>
public static class FirstRunGatewayServiceCollectionExtensions
{
    /// <summary>Adds <see cref="GatewayDefaultAgentRoutes"/> as the <see cref="IDefaultAgentRoutes"/> and <see cref="DefaultAgentPolicyGrant"/>.</summary>
    public static IServiceCollection AddFirstRunGatewayRoutes(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<IDefaultAgentRoutes, GatewayDefaultAgentRoutes>();
        services.TryAddSingleton<DefaultAgentPolicyGrant>();
        return services;
    }
}
