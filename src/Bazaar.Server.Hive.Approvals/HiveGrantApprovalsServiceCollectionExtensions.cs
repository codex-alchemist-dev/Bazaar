using System.Data.Common;
using Bazaar.Server.Hive.Abstractions;
using Bazaar.Server.ReadAudit.Npgsql;
using Bazaar.Server.Security.Approvals;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace Bazaar.Server.Hive.Approvals;

/// <summary>Registration for Hive grant approvals over the owner queue.</summary>
public static class HiveGrantApprovalsServiceCollectionExtensions
{
    /// <summary>
    /// Replaces the fail-closed <c>IHiveGrantApprovals</c> default with <see cref="OwnerQueueHiveGrantApprovals"/> and adds the
    /// sweeper that applies approved changes. Needs <c>IOwnerApprovalEnqueuer</c>, <c>IApprovalStore</c> and <c>ApprovalService</c>
    /// from the host's Security registration. Without the enqueuer the fail-closed default stays in force; without the store or
    /// service the sweeper idles. Nothing here throws at startup. Order-independent
    /// with the Hive component, because it replaces rather than adds.
    /// </summary>
    public static IServiceCollection AddBazaarHiveGrantApprovals(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<IHiveGrantChangeLedger>(sp =>
            (sp.GetService<AuditedDbDataSource>() ?? (DbDataSource?)sp.GetService<NpgsqlDataSource>()) is { } dataSource
                ? new PgHiveGrantChangeLedger(dataSource)
                : new InMemoryHiveGrantChangeLedger());
        services.Replace(ServiceDescriptor.Singleton<IHiveGrantApprovals>(sp =>
            sp.GetService<IOwnerApprovalEnqueuer>() is { } enqueuer
                ? new OwnerQueueHiveGrantApprovals(enqueuer, sp.GetRequiredService<IHiveGrantChangeLedger>())
                : new DenyAllHiveGrantApprovals()));
        services.AddHostedService<HiveGrantApprovalSweeper>();
        return services;
    }
}
