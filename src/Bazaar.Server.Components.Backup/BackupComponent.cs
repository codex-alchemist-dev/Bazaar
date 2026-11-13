using Bazaar.Contracts.Backup;
using Bazaar.Server.Components;
using Bazaar.Server.Components.Backup.Engines;
using Bazaar.Server.Components.Backup.Jobs;
using Bazaar.Server.Components.Backup.Processes;
using Bazaar.Server.Jobs;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace Bazaar.Server.Components.Backup;

/// <summary>
/// Backup as a Bazaar Component (docs/design/33-backup-dr-and-export.md, docs/design/BAZAAR-COMPONENTS.md). Registers the run log that the
/// status card and alerts read. It depends on no other component. Base backups are opt-in and use the shared database connection
/// plus operator-supplied PostgreSQL binary and storage paths.
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

        var options = new BaseBackupJobOptions();
        configuration.GetSection(BaseBackupJobOptions.SectionName).Bind(options);
        if (!options.Enabled)
            return;

        if (string.IsNullOrWhiteSpace(options.SetsRoot))
            throw new InvalidOperationException($"{BaseBackupJobOptions.SectionName}:SetsRoot is required when backups are enabled.");
        if (string.IsNullOrWhiteSpace(options.PgBinDirectory))
            throw new InvalidOperationException($"{BaseBackupJobOptions.SectionName}:PgBinDirectory is required when backups are enabled.");
        if (options.BaseSetsKept < 1)
            throw new InvalidOperationException($"{BaseBackupJobOptions.SectionName}:BaseSetsKept must be at least one.");

        services.AddSingleton(options);
        services.TryAddSingleton<IProcessRunner, SystemProcessRunner>();
        services.TryAddSingleton<IBackupEngine>(sp =>
        {
            var dataSource = sp.GetRequiredService<NpgsqlDataSource>();
            var time = sp.GetRequiredService<TimeProvider>();
            return new PgBasebackupEngine(
                sp.GetRequiredService<IProcessRunner>(),
                options.PgBinDirectory,
                dataSource.ConnectionString,
                time);
        });
        services.TryAddSingleton<BaseBackupJob>(sp => new BaseBackupJob(
            sp.GetRequiredService<IBackupEngine>(),
            options.SetsRoot,
            options.BaseSetsKept,
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<IBackupRunLog>(),
            sp.GetRequiredService<IProcessRunner>(),
            options.ArchiveDirectory,
            options.PgBinDirectory));
        services.AddSingleton<BaseBackupJobHandler>();
        services.AddSingleton<IJobHandler>(sp => sp.GetRequiredService<BaseBackupJobHandler>());
        services.AddSingleton(new RecurringJob(BaseBackupJobHandler.JobKind, options.Interval));
    }
}
