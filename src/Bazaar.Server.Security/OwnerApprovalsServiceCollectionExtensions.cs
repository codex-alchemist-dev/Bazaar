using Bazaar.Server.Security.Approvals;
using Bazaar.Server.Security.Approvals.Db;
using Bazaar.Server.Security.Policy;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace Bazaar.Server.Security;

/// <summary>Registration for the durable owner approvals (doc 15, "Approval flow"): the store, the service and the enqueue port.</summary>
public static class OwnerApprovalsServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="PostgresApprovalStore"/> as <see cref="IApprovalStore"/> over the shared <see cref="NpgsqlDataSource"/>,
    /// <see cref="ApprovalService"/>, the per-agent request cap bucket store and the <see cref="IOwnerApprovalEnqueuer"/>. Every
    /// registration is TryAdd, so a host or a test can substitute one first.
    /// </summary>
    /// <remarks>
    /// The host must register the <see cref="NpgsqlDataSource"/> and the owner key registry (<c>AddBazaarApprovals</c>). Without a
    /// data source the store, and so the enqueuer and the service, cannot be resolved: the host fails closed. The default bucket
    /// store is in memory, so request counts restart with the process; this is the same store the policy engine uses today.
    /// </remarks>
    public static IServiceCollection AddBazaarOwnerApprovals(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<IApprovalStore>(sp => new PostgresApprovalStore(sp.GetRequiredService<NpgsqlDataSource>()));
        services.TryAddSingleton<IRateBucketStore, InMemoryRateBucketStore>();
        services.TryAddSingleton(new ApprovalOptions());
        services.TryAddSingleton<ApprovalService>();
        return services.AddOwnerApprovalEnqueue();
    }
}
