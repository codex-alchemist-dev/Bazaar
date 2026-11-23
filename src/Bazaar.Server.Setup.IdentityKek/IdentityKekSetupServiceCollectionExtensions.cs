using Microsoft.Extensions.DependencyInjection;

namespace Bazaar.Server.Setup.IdentityKek;

/// <summary>Registers the identity KEK setup step.</summary>
public static class IdentityKekSetupServiceCollectionExtensions
{
    /// <summary>Registers <see cref="IdentityKekHostConfigStep"/> as an <see cref="ISetupStep"/>. <paramref name="dataRoot"/> returns the host data root.</summary>
    public static IServiceCollection AddBazaarSetupIdentityKek(this IServiceCollection services, Func<IServiceProvider, string> dataRoot)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(dataRoot);
        services.AddSingleton<ISetupStep>(sp => new IdentityKekHostConfigStep(() => dataRoot(sp)));
        return services;
    }
}
