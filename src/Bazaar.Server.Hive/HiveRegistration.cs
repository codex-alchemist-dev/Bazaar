using Bazaar.Server.Hive.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Bazaar.Server.Hive;

public static class HiveRegistration
{
    /// <summary>
    /// Registers the Hive stores and flow. The host composes this and must register an <c>NpgsqlDataSource</c> and adapters for
    /// <see cref="IHiveSanitizer"/>, <see cref="IHiveVaultWriter"/>, <see cref="IHiveAudit"/> and <see cref="IHiveSettings"/> (the
    /// narrow ports owned by docs 08, 18 and 15), and a <see cref="TimeProvider"/>.
    /// </summary>
    public static IServiceCollection AddBazaarHive(this IServiceCollection services)
    {
        services.TryAddSingleton<IHiveGrantStore, PostgresHiveGrantStore>();
        services.TryAddSingleton<IHiveCopyStore, PostgresHiveCopyStore>();
        services.TryAddSingleton<IHiveGrantPolicy, HiveGrantPolicy>();
        services.TryAddSingleton<HiveGrantService>();
        services.TryAddSingleton<HiveCopyService>();
        return services;
    }
}
