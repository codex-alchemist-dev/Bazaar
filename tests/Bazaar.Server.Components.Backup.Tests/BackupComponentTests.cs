using Bazaar.Server.Components;
using Bazaar.Server.Components.Backup.Jobs;
using Bazaar.Server.Jobs;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Bazaar.Server.Components.Backup.Tests;

public sealed class BackupComponentTests
{
    [Fact]
    public void Manifest_is_a_feature_with_no_dependencies()
    {
        var manifest = BackupComponent.ManifestDefinition;

        Assert.Equal("backup", manifest.Id);
        Assert.Equal(ComponentKind.Feature, manifest.Kind);
        Assert.Empty(manifest.DependsOn);
    }

    [Fact]
    public void Registration_resolves_the_run_log_in_a_validated_host()
    {
        var services = new ServiceCollection();
        new BackupComponent().AddServices(services, new ConfigurationBuilder().Build());

        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });

        Assert.IsType<InMemoryBackupRunLog>(provider.GetRequiredService<IBackupRunLog>());
    }

    [Fact]
    public void Base_backup_is_disabled_by_default()
    {
        var services = new ServiceCollection();
        new BackupComponent().AddServices(services, new ConfigurationBuilder().Build());

        Assert.DoesNotContain(services, d => d.ServiceType == typeof(IJobHandler));
        Assert.DoesNotContain(services, d => d.ServiceType == typeof(RecurringJob));
    }

    [Fact]
    public void Enabled_base_backup_registers_engine_handler_and_schedule()
    {
        using var dataSource = NpgsqlDataSource.Create("Host=127.0.0.1;Port=1;Database=bazaar");
        var services = new ServiceCollection();
        services.AddSingleton(dataSource);
        services.AddSingleton(TimeProvider.System);
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [$"{BaseBackupJobOptions.SectionName}:Enabled"] = "true",
                [$"{BaseBackupJobOptions.SectionName}:SetsRoot"] = "/var/lib/bazaar/backups",
                [$"{BaseBackupJobOptions.SectionName}:PgBinDirectory"] = "/usr/lib/postgresql/18/bin",
                [$"{BaseBackupJobOptions.SectionName}:Interval"] = "1.00:00:00",
            })
            .Build();

        new BackupComponent().AddServices(services, configuration);

        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
        Assert.IsType<BaseBackupJobHandler>(provider.GetRequiredService<IJobHandler>());
        Assert.Equal(BaseBackupJobHandler.JobKind, provider.GetRequiredService<RecurringJob>().Kind);
        Assert.IsType<PgBasebackupEngine>(provider.GetRequiredService<IBackupEngine>());
    }
}
