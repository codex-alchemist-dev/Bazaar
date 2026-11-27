using Cdax.Core;
using Bazaar.Server.BrowserPool.Abstractions;
using Bazaar.Server.Security.Audit;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Bazaar.Server.BrowserPool.Audit;

/// <summary>
/// Joins the browser pool to the audit log as a Bazaar Component (docs/design/59, section 7). It registers
/// <see cref="AuditLogPoolAuditSink"/> as the <see cref="IPoolAuditSink"/>, replacing the pool's no-op default whichever component
/// is added first. The audit log is resolved when the sink is first needed; without one every write is dropped and counted.
/// </summary>
public sealed class BrowserPoolAuditComponent : ICdaxModule
{
    /// <summary>The manifest, available without constructing the component.</summary>
    public static ComponentManifest ManifestDefinition { get; } = new(
        Id: "browser-pool-audit",
        Version: "0.1.0",
        Kind: ComponentKind.Adapter,
        DependsOn: ["browser-pool"],
        Provides: ["browser-pool:audit"],
        SoftDependsOn: ["audit"]);

    /// <inheritdoc />
    public ComponentManifest Manifest => ManifestDefinition;

    /// <inheritdoc />
    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddSingleton(PoolAuditOptions.Bind(configuration));
        services.TryAddSingleton(TimeProvider.System);

        // Replace, not TryAdd: the pool registers its no-op sink with TryAdd, so a replace works in either order, and a later host
        // registration still wins.
        services.Replace(ServiceDescriptor.Singleton<IPoolAuditSink>(sp => new AuditLogPoolAuditSink(
            sp.GetService<IAuditLog>(),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<PoolAuditOptions>())));
    }
}
