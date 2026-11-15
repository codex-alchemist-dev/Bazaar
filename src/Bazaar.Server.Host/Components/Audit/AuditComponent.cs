using Bazaar.Server.Components;
using Bazaar.Server.Host.Startup;
using Bazaar.Server.Security.Audit;
using Bazaar.Server.Security.Audit.Db;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace Bazaar.Server.Host.Components.Audit;

/// <summary>
/// The Postgres audit log (doc 15) as a Bazaar Component. Registers <see cref="IAuditLog"/> on the shared
/// <see cref="NpgsqlDataSource"/>. It depends on no other component; the kernel's <c>DatabaseConnection</c> provides the data source.
/// </summary>
public sealed class AuditComponent : IBazaarComponent
{
    public static ComponentManifest ManifestDefinition { get; } = new(
        Id: "audit",
        Version: "0.1.0",
        Kind: ComponentKind.Feature,
        DependsOn: [],
        Provides: ["feature:audit"]);

    public ComponentManifest Manifest => ManifestDefinition;

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        services.TryAddSingleton(sp => SharedDataSource(sp));
        services.TryAddSingleton<PostgresAuditLog>();
        services.TryAddSingleton<IAuditLog>(sp => sp.GetRequiredService<PostgresAuditLog>());
        services.TryAddSingleton<ITransactionalAuditLog>(sp => sp.GetRequiredService<PostgresAuditLog>());
    }

    /// <summary>
    /// The one <see cref="NpgsqlDataSource"/>. <see cref="DatabaseConnection"/> resolves the secret once and caches the source,
    /// so this returns that same instance. The resolve is synchronous because the factory is; it runs once, at first use.
    /// </summary>
    private static NpgsqlDataSource SharedDataSource(IServiceProvider sp) =>
        sp.GetRequiredService<DatabaseConnection>().GetAsync(CancellationToken.None).AsTask().GetAwaiter().GetResult();
}
