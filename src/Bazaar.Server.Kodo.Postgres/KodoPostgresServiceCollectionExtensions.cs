using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Bazaar.Server.Kodo.Postgres;

/// <summary>Registers the durable Kodo journal store.</summary>
public static class KodoPostgresServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="PgKodoJournalStore"/> as <see cref="IKodoJournalStore"/> (TryAdd). The Kodo module then replays its local
    /// journal into it and falls back to the memory and spill journal when it is not registered or the database is down. Needs an
    /// <c>NpgsqlDataSource</c> in the container.
    /// </summary>
    public static IServiceCollection AddBazaarKodoPostgres(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<IKodoJournalStore, PgKodoJournalStore>();
        return services;
    }
}
