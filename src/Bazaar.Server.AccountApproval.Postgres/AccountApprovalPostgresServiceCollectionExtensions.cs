using Bazaar.Server.AccountApproval.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace Bazaar.Server.AccountApproval.Postgres;

/// <summary>Registers the durable approval store. The caller registers the shared <see cref="NpgsqlDataSource"/> first.</summary>
public static class AccountApprovalPostgresServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="PostgresAccountApprovalStore"/> as <see cref="IAccountApprovalStore"/>, replacing any earlier registration.
    /// It wins over the in-memory default whether it is called before or after the account approval component's services, because the
    /// component only adds its default when no store is registered.
    /// </summary>
    public static IServiceCollection AddBazaarAccountApprovalPostgres(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.Replace(ServiceDescriptor.Singleton<IAccountApprovalStore, PostgresAccountApprovalStore>());
        return services;
    }
}
