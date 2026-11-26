using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace Bazaar.Server.AuditSinks.Postgres;

/// <summary>Registers the Postgres adapters of the audit sinks module.</summary>
public static class AuditSinksPostgresServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="PostgresAuditSinkCursorStore"/> and <see cref="PostgresAuditSinkSource"/> (TryAdd, so a caller's own wins).
    /// The caller registers the RAW <see cref="NpgsqlDataSource"/> first: the source must not read through the audited one.
    /// </summary>
    public static IServiceCollection AddBazaarAuditSinksPostgres(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<IAuditSinkCursorStore, PostgresAuditSinkCursorStore>();
        services.TryAddSingleton<IAuditSinkSource, PostgresAuditSinkSource>();
        return services;
    }
}
