using Bazaar.Components.ManagedPostgres.Maintenance;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Bazaar.Components.ManagedPostgres.Tests;

public sealed class ManagedPostgresBackupServiceTests
{
    [Fact]
    public void Dumps_are_on_by_default_daily_and_keep_seven()
    {
        var backup = new ManagedPostgresOptions().Backup;

        Assert.True(backup.Enabled);
        Assert.Equal(TimeSpan.FromHours(24), backup.Interval);
        Assert.Equal(7, backup.KeepDumps);
    }

    [Fact]
    public void Dumps_go_under_the_data_root_unless_a_folder_is_configured()
    {
        var defaults = new ManagedPostgresOptions { DataRoot = Path.Combine("data", "root") };
        var custom = new ManagedPostgresOptions { DataRoot = Path.Combine("data", "root"), Backup = new ManagedPostgresBackupOptions { Directory = Path.Combine("mnt", "dumps") } };

        Assert.Equal(Path.Combine("data", "root", "backups"), ManagedPostgresBackupService.BackupDirectory(defaults));
        Assert.Equal(Path.Combine("mnt", "dumps"), ManagedPostgresBackupService.BackupDirectory(custom));
    }

    [Fact]
    public void The_dump_connection_takes_the_running_cluster_and_its_major()
    {
        var running = new ManagedPostgresConnectionInfo("127.0.0.1", 54321, "bazaar_admin", "pw", "postgres");

        var connection = ManagedPostgresBackupService.ToConnection(running, 18);

        Assert.Equal(new PgConnection(18, 54321, "bazaar_admin", "pw", "postgres"), connection);
    }

    [Fact]
    public async Task Nothing_is_dumped_in_external_mode()
    {
        var service = Service(new ManagedPostgresOptions { Mode = DatabaseMode.External }, new FakePostgresCommandRunner());

        Assert.Null(await service.BackupOnceAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Nothing_is_dumped_before_the_managed_cluster_is_running()
    {
        var runner = new FakePostgresCommandRunner();
        var service = Service(new ManagedPostgresOptions { Mode = DatabaseMode.Managed, DataRoot = Path.Combine("data", "root") }, runner);

        Assert.Null(await service.BackupOnceAsync(TestContext.Current.CancellationToken));
        Assert.Empty(runner.Runs);
    }

    private static ManagedPostgresBackupService Service(ManagedPostgresOptions options, FakePostgresCommandRunner runner) =>
        new(
            Options.Create(options),
            runner,
            new ManagedPostgresEndpoint(),
            TimeProvider.System,
            NullLogger<ManagedPostgresBackupService>.Instance);
}
