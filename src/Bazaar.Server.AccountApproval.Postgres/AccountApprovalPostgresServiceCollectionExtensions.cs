using Bazaar.Server.AccountApproval.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace Bazaar.Server.AccountApproval.Postgres;

/// <summary>Registers the durable approval store. The caller registers the shared <see cref="NpgsqlDataSource"/> first.</summary>
public static class AccountApprovalPostgresServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="PostgresAccountApprovalStore"/> as <see cref="IAccountApprovalStore"/> (TryAdd). Call it before the account
    /// approval component's services, so this store wins over the in-memory default.
    /// </summary>
    public static IServiceCollection AddBazaarAccountApprovalPostgres(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<IAccountApprovalStore, PostgresAccountApprovalStore>();
        return services;
    }
}
