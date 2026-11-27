using Cdax.Core;
using Bazaar.Server.Settings;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Bazaar.Server.BackupPolicy;

/// <summary>
/// The backup policy settings as a Bazaar Component (BAZAAR-COMPONENTS.md). Registers only the settings contribution: no I/O, no
/// scheduler. The Backup module reads the keys when it runs backups; setup writes them (doc 45 step 8).
/// </summary>
public sealed class BackupPolicyComponent : ICdaxModule
{
    public static ComponentManifest ManifestDefinition { get; } = new(
        Id: "backup-policy",
        Version: "0.1.0",
        Kind: ComponentKind.Feature,
        DependsOn: [],
        Provides: ["feature:backup-policy"]);

    public ComponentManifest Manifest => ManifestDefinition;

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        services.AddSingleton<ISettingContribution, BackupPolicySettingContribution>();
    }
}
