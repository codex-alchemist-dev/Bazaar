using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Bazaar.Server.SchemaIdentity;

public static class SchemaIdentityServiceCollectionExtensions
{
    /// <summary>Registers the Postgres-backed <see cref="ISchemaIdentityProvider"/>. Needs the host's <c>NpgsqlDataSource</c>.</summary>
    public static IServiceCollection AddBazaarSchemaIdentity(this IServiceCollection services)
    {
        services.TryAddSingleton<ISchemaIdentityProvider, PostgresSchemaIdentityProvider>();
        return services;
    }
}
