using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace Bazaar.Server.Setup.Postgres;

/// <summary>Registers the durable setup services. The caller registers the shared <see cref="NpgsqlDataSource"/> first.</summary>
public static class SetupPostgresServiceCollectionExtensions
{
    /// <summary>Registers <see cref="PostgresSetupInstanceLock"/> and <see cref="PostgresApplyResultStore"/> (TryAdd, so a caller's own wins).</summary>
    public static IServiceCollection AddBazaarSetupPostgres(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<ISetupInstanceLock, PostgresSetupInstanceLock>();
        services.TryAddSingleton<IApplyResultStore, PostgresApplyResultStore>();
        return services;
    }
}
