using Microsoft.Extensions.DependencyInjection;

namespace Bazaar.Server.Vault.Search;

/// <summary>
/// Registration for the Vault search Bazaar Component. Only the host composition calls this.
/// TODO(component): move into the search component's own project once the component model PR merges.
/// </summary>
public static class VaultSearchServiceCollectionExtensions
{
    public static IServiceCollection AddBazaarVaultSearch(this IServiceCollection services, VaultSearchOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton(options ?? new VaultSearchOptions());
        services.AddSingleton<Data.VaultSearchRepository>();
        services.AddSingleton<VaultSearchService>();
        return services;
    }
}
