using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Bazaar.Server.Kodo.Postgres;

/// <summary>Registers the durable Kodo journal store.</summary>
public static class KodoPostgresServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="PgKodoJournalStore"/> as <see cref="IKodoJournalStore"/> and <see cref="IKodoCheckpointStore"/> (TryAdd). The Kodo module then replays its local
    /// journal into it and falls back to the memory and spill journal when it is not registered or the database is down. Needs an
    /// <c>NpgsqlDataSource</c> in the container.
    /// </summary>
    public static IServiceCollection AddBazaarKodoPostgres(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<PgKodoJournalStore>();
        services.TryAddSingleton<IKodoJournalStore>(sp => sp.GetRequiredService<PgKodoJournalStore>());
        services.TryAddSingleton<IKodoCheckpointStore>(sp => sp.GetRequiredService<PgKodoJournalStore>());
        return services;
    }
}
