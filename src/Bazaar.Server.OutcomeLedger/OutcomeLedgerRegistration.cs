using Bazaar.Server.OutcomeLedger.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace Bazaar.Server.OutcomeLedger;

/// <summary>Composition of the outcome ledger module.</summary>
public static class OutcomeLedgerRegistration
{
    /// <summary>
    /// Registers the recorder. The store is Postgres when an <c>NpgsqlDataSource</c> is registered (tables from common/0013),
    /// otherwise in-memory. Register <see cref="IOutcomeListener"/>s (for example the ward watcher adapter) before resolving it.
    /// </summary>
    public static IServiceCollection AddBazaarOutcomeLedger(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton(new OutcomeWeights());
        services.TryAddSingleton<IOutcomeLedgerStore>(sp =>
            sp.GetService<NpgsqlDataSource>() is { } dataSource ? new PgOutcomeLedgerStore(dataSource) : new InMemoryOutcomeLedgerStore());
        services.TryAddSingleton<IOutcomeRecorder>(sp => new OutcomeRecorder(
            sp.GetRequiredService<IOutcomeLedgerStore>(),
            sp.GetRequiredService<OutcomeWeights>(),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetServices<IOutcomeListener>()));
        return services;
    }
}
