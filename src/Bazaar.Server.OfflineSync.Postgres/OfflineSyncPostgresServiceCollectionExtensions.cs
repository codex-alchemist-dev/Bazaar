using System.Reflection;
using Bazaar.OfflineSync;
using Bazaar.Server.OfflineSync.Rules;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace Bazaar.Server.OfflineSync.Postgres;

/// <summary>Registers the durable offline sync store and merge state.</summary>
public static class OfflineSyncPostgresServiceCollectionExtensions
{
    /// <summary>
    /// The assembly that embeds this module's migrations under <c>Sql/components/offline_sync</c>, for the host's migration source.
    /// </summary>
    public static Assembly MigrationAssembly => typeof(PostgresOfflineSyncStore).Assembly;

    /// <summary>
    /// Registers the offline sync module and replaces its in-memory defaults (store, audit, write scope) with Postgres ones, and
    /// registers the Postgres merge states and review sink for the rules. The host must register one shared
    /// <see cref="NpgsqlDataSource"/>. Order does not matter: replacements win whether this runs before or after
    /// <c>AddBazaarOfflineSync</c>.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The same collection.</returns>
    public static IServiceCollection AddBazaarOfflineSyncPostgres(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddBazaarOfflineSync();
        services.Replace(ServiceDescriptor.Singleton<IOfflineSyncStore>(static sp => new PostgresOfflineSyncStore(sp.GetRequiredService<NpgsqlDataSource>())));
        services.Replace(ServiceDescriptor.Singleton<IOfflineMergeAudit>(static sp => new PostgresOfflineMergeAudit(sp.GetRequiredService<NpgsqlDataSource>())));
        services.Replace(ServiceDescriptor.Singleton<IOfflineWriteScopeFactory>(static sp => new PostgresOfflineWriteScopeFactory(sp.GetRequiredService<NpgsqlDataSource>())));
        services.Replace(ServiceDescriptor.Singleton<IReviewSink>(static sp => new PostgresReviewSink(sp.GetRequiredService<NpgsqlDataSource>())));
        services.Replace(ServiceDescriptor.Singleton<IGcAppendState>(static sp => new PostgresGcAppendState(sp.GetRequiredService<NpgsqlDataSource>())));
        services.Replace(ServiceDescriptor.Singleton<IGcEditState>(static sp => new PostgresGcEditState(sp.GetRequiredService<NpgsqlDataSource>())));
        services.Replace(ServiceDescriptor.Singleton<IKanbanFieldState>(static sp => new PostgresKanbanFieldState(sp.GetRequiredService<NpgsqlDataSource>())));
        services.Replace(ServiceDescriptor.Singleton<IKanbanEdgeState>(static sp => new PostgresKanbanEdgeState(sp.GetRequiredService<NpgsqlDataSource>())));
        services.Replace(ServiceDescriptor.Singleton<ILastWriterWinsState>(static sp => new PostgresLastWriterWinsState(sp.GetRequiredService<NpgsqlDataSource>())));
        services.Replace(ServiceDescriptor.Singleton<IAttachmentState>(static sp => new PostgresAttachmentState(sp.GetRequiredService<NpgsqlDataSource>())));
        services.Replace(ServiceDescriptor.Singleton<IAuditActionSink>(static sp => new PostgresAuditActionSink(sp.GetRequiredService<NpgsqlDataSource>())));
        return services;
    }
}
