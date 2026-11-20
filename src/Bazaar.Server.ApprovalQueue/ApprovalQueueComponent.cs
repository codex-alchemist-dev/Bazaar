using Bazaar.Core;
using Bazaar.Server.WebAdmin;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace Bazaar.Server.ApprovalQueue;

/// <summary>
/// The shared approvals queue as a Bazaar Component (docs/design/51-web-admin-module.md, approvals queue). Owns the
/// <c>approval_queue</c> migration namespace (<c>Sql/approval_queue/</c>).
/// </summary>
public sealed class ApprovalQueueComponent : IBazaarComponent
{
    public static ComponentManifest ManifestDefinition { get; } = new(
        Id: "approval-queue",
        Version: "0.1.0",
        Kind: ComponentKind.Feature,
        DependsOn: ["roles", "audit"],
        Provides: ["feature:approval-queue"]);

    public ComponentManifest Manifest => ManifestDefinition;

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        services.AddBazaarApprovalQueue();
    }
}

public static class ApprovalQueueServiceCollectionExtensions
{
    /// <summary>
    /// Registers the Postgres store (over the shared <see cref="NpgsqlDataSource"/>, resolved lazily), the queue and the admin
    /// panel's <see cref="IAdminApprovalSource"/>. Needs <c>IPermissionChecker</c>, <c>IRoleStore</c> and <c>IAuditLog</c> from the
    /// roles and audit components.
    /// </summary>
    public static IServiceCollection AddBazaarApprovalQueue(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IApprovalStore>(sp => new PostgresApprovalStore(sp.GetRequiredService<NpgsqlDataSource>()));
        services.TryAddSingleton<IApprovalQueue, ApprovalQueueService>();
        services.TryAddSingleton<IAdminApprovalSource, AdminApprovalSource>();
        return services;
    }
}
