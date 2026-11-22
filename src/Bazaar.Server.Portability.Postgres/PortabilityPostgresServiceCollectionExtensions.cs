using Bazaar.Server.Portability;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Bazaar.Server.Portability.Postgres;

/// <summary>Registers the durable import job store. Call it before any in-memory default, so the durable store wins.</summary>
public static class PortabilityPostgresServiceCollectionExtensions
{
    /// <summary>Registers <see cref="PostgresImportJobStore"/> as <see cref="IImportJobStore"/> (TryAdd). Needs an <c>NpgsqlDataSource</c> in the container.</summary>
    public static IServiceCollection AddBazaarPortabilityPostgres(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<IImportJobStore, PostgresImportJobStore>();
        return services;
    }
}
