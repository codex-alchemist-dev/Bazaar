using Bazaar.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Bazaar.Server.OfflineSync;

/// <summary>
/// Offline sync as a Bazaar Component (docs/design/56-offline-sync-module.md section 2). The merge rules of the other modules are
/// optional: a missing dependency disables only the rule for its data class, so every dependency here is soft.
/// </summary>
public sealed class OfflineSyncComponent : IBazaarComponent
{
    /// <summary>The manifest, available without an instance for tests.</summary>
    public static ComponentManifest ManifestDefinition { get; } = new(
        Id: OfflineSyncModule.ComponentId,
        Version: "0.1.0",
        Kind: ComponentKind.Feature,
        DependsOn: [],
        Provides: ["feature:offline-sync"],
        SoftDependsOn: ["gc", "kanban", "vault", "audit"]);

    /// <inheritdoc />
    public ComponentManifest Manifest => ManifestDefinition;

    /// <inheritdoc />
    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddBazaarOfflineSync();
        services.TryAddSingleton<OfflineSyncBatchHandler>();
    }
}
