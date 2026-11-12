using Bazaar.Components.ManagedPostgres.Bundle;
using Bazaar.Components.ManagedPostgres.Secrets;
using Microsoft.Extensions.Options;

namespace Bazaar.Components.ManagedPostgres.Maintenance;

/// <summary>
/// The operator's maintenance commands for the managed cluster: a verified dump on demand, and a planned major upgrade.
/// Both refuse to run while the Bazaar host is using the cluster (its port is listening), so a command never stops a server
/// another process depends on. When the cluster is down, a command starts it, does its work, and stops it again.
/// The database that is dumped is the one the host uses (<see cref="ManagedPostgresConnectionInfo.Database"/>).
/// </summary>
public sealed class ManagedPostgresMaintenance(
    IOptions<ManagedPostgresOptions> options,
    IPostgresCommandRunner runner,
    IManagedPasswordStore passwords,
    TimeProvider time)
{
    private const string _database = "postgres";

    /// <summary>Takes one verified dump of the configured major and keeps the newest <paramref name="keep"/>.</summary>
    public async Task<PgDumpBackup> BackupAsync(int keep, CancellationToken cancellationToken)
    {
        var settings = RequireManaged();
        var password = await RequirePasswordAsync(cancellationToken);
        var layout = new BundleLayout(settings.BundleRoot, settings.DataRoot);
        var job = new PgDumpBackupJob(runner, layout, time);
        var connection = Connection(settings.Major, settings.Port, settings.SuperuserName, password);
        var directory = ManagedPostgresBackupService.BackupDirectory(settings);

        if (await PgUpgradeRunner.IsListeningAsync(settings.Port, cancellationToken))
        {
            return await job.RunAsync(connection, directory, keep, cancellationToken);
        }

        await using var cluster = new PostgresProcessController(ProcessOptions(settings, settings.Major), runner);
        await cluster.StartAsync(cancellationToken);
        try
        {
            return await job.RunAsync(connection, directory, keep, cancellationToken);
        }
        finally
        {
            await cluster.StopAsync(CancellationToken.None);
        }
    }

    /// <summary>
    /// Upgrades the managed cluster from its configured major to <paramref name="toMajor"/>. Refused while the host runs the
    /// cluster. The new cluster is initialised with the stored password, so the one password works for both. After this returns,
    /// set Bazaar:Database:Major to <paramref name="toMajor"/> and start the host.
    /// </summary>
    public async Task<PgUpgradeResult> UpgradeAsync(int toMajor, int keep, CancellationToken cancellationToken)
    {
        var settings = RequireManaged();
        var from = settings.Major;
        if (toMajor != from + 1)
        {
            throw new PgMaintenanceException($"pg_upgrade moves one major at a time. Upgrade {from} to {from + 1} first, then continue.");
        }

        if (await PgUpgradeRunner.IsListeningAsync(settings.Port, cancellationToken))
        {
            throw new PgMaintenanceException($"The PostgreSQL cluster on port {settings.Port} is running. Stop the Bazaar host first, so the upgrade stops only the cluster it owns.");
        }

        var password = await RequirePasswordAsync(cancellationToken);
        var layout = new BundleLayout(settings.BundleRoot, settings.DataRoot);

        if (!File.Exists(Path.Combine(layout.Resolve(toMajor).DataDirectory, "PG_VERSION")))
        {
            await using var fresh = new PostgresProcessController(ProcessOptions(settings, toMajor), runner);
            await fresh.InitDbWithPasswordAsync(password, cancellationToken);
        }

        await using var old = new PostgresProcessController(ProcessOptions(settings, from), runner);
        await old.StartAsync(cancellationToken);

        var upgrader = new PgUpgradeRunner(runner, layout, new PgDumpBackupJob(runner, layout, time));
        var request = new PgUpgradeRequest(
            Connection(from, settings.Port, settings.SuperuserName, password),
            toMajor,
            ManagedPostgresBackupService.BackupDirectory(settings),
            keep);

        return await upgrader.RunAsync(request, ct => old.StopAsync(ct), cancellationToken);
    }

    private ManagedPostgresOptions RequireManaged()
    {
        var settings = options.Value;
        if (settings.Mode != DatabaseMode.Managed)
        {
            throw new PgMaintenanceException("Managed mode is off. Set Bazaar:Database:Mode to Managed before running this command.");
        }

        return settings;
    }

    private async Task<string> RequirePasswordAsync(CancellationToken cancellationToken) =>
        await passwords.LoadAsync(cancellationToken)
        ?? throw new PgMaintenanceException("No managed superuser password is stored. Start the host once so it can initialise the cluster.");

    private static PgConnection Connection(int major, int port, string user, string password) =>
        new(major, port, user, password, _database);

    private static PostgresProcessOptions ProcessOptions(ManagedPostgresOptions settings, int major)
    {
        var paths = new BundleLayout(settings.BundleRoot, settings.DataRoot).Resolve(major);
        return new PostgresProcessOptions
        {
            BinDirectory = paths.Bin,
            DataDirectory = paths.DataDirectory,
            Port = settings.Port,
            ListenAddress = settings.ListenAddress,
            SuperuserName = settings.SuperuserName,
        };
    }
}
