using Bazaar.Server.Components;
using Bazaar.Server.Components.Backup.Jobs;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Bazaar.Server.Components.Backup;

/// <summary>
/// Backup as a Bazaar Component (docs/design/33-backup-dr-and-export.md, docs/design/BAZAAR-COMPONENTS.md). Registers the run log that the
/// status card and alerts read. It depends on no other component. The base-backup job and restore runner are constructed by the
/// operator's scheduled entry point, not by DI, until the schedule and run-record table land.
/// TODO(component): persist run records through this component's own migration namespace (components/backup/NNNN_name.sql).
/// </summary>
public sealed class BackupComponent : IBazaarComponent
{
    public static ComponentManifest ManifestDefinition { get; } = new(
        Id: "backup",
        Version: "0.1.0",
        Kind: ComponentKind.Feature,
        DependsOn: [],
        Provides: ["feature:backup"]);

    public ComponentManifest Manifest => ManifestDefinition;

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        services.TryAddSingleton<IBackupRunLog, InMemoryBackupRunLog>();
    }
}
