using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Bazaar.Components.ManagedPostgres;

/// <summary>The one public registration for the managed PostgreSQL Bazaar Component. Only the host composition root calls it.</summary>
public static class ManagedPostgresServiceCollectionExtensions
{
    // TODO(component): move this registration behind the component model when it lands.
    public static IServiceCollection AddBazaarManagedPostgres(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<ManagedPostgresOptions>()
            .Bind(configuration.GetSection(ManagedPostgresOptions.SectionName))
            .ValidateOnStart();

        return services;
    }
}
