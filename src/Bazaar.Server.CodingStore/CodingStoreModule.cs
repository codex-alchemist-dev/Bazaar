using Bazaar.Server.CodingStore.Data;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace Bazaar.Server.CodingStore;

/// <summary>Registration for the coding harness tables.</summary>
public static class CodingStoreModule
{
    /// <summary>
    /// Registers <see cref="PostgresToolCallStore"/> (as <see cref="IToolCallLog"/> and <see cref="IToolCallReader"/>) and
    /// <see cref="PostgresCheckRunStore"/>. Needs the shared <see cref="NpgsqlDataSource"/>; a missing one fails at resolution.
    /// </summary>
    public static IServiceCollection AddBazaarCodingStore(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton(sp => new PostgresToolCallStore(sp.GetRequiredService<NpgsqlDataSource>()));
        services.TryAddSingleton<IToolCallLog>(sp => sp.GetRequiredService<PostgresToolCallStore>());
        services.TryAddSingleton<IToolCallReader>(sp => sp.GetRequiredService<PostgresToolCallStore>());
        services.TryAddSingleton<ICheckRunStore>(sp => new PostgresCheckRunStore(sp.GetRequiredService<NpgsqlDataSource>()));
        return services;
    }
}
