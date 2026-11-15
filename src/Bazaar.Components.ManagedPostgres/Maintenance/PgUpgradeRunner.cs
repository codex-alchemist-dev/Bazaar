using System.Net.Sockets;
using Bazaar.Components.ManagedPostgres.Bundle;

namespace Bazaar.Components.ManagedPostgres.Maintenance;

/// <summary>One planned major upgrade. <paramref name="Old"/> must describe the running old cluster, so the pre-upgrade dump can connect.</summary>
public sealed record PgUpgradeRequest(PgConnection Old, int ToMajor, string BackupDirectory, int KeepDumps);

/// <summary>What the upgrade left behind: the verified dump taken before it, and the majors involved.</summary>
public sealed record PgUpgradeResult(PgDumpBackup Backup, int FromMajor, int ToMajor);

/// <summary>
/// Runs the planned <c>pg_upgrade</c> (packaging.md section 3, lifecycle step 4). The order is fixed and every step
/// stops the run on failure:
/// <list type="number">
/// <item>Both data directories exist; the new one was initialised by initdb with the new bundle.</item>
/// <item>A verified <c>pg_dump</c> is written while the old cluster still runs. This is the restore point.</item>
/// <item>The caller stops the old cluster, and the port is confirmed closed.</item>
/// <item><c>pg_upgrade --check</c>. Any failure stops here, before the data files are touched.</item>
/// <item>The real upgrade, in copy mode, so the old data directory stays intact until the operator removes it.</item>
/// </list>
/// The caller starts the new cluster afterwards; this runner never starts a server.
/// </summary>
public sealed class PgUpgradeRunner(IPostgresCommandRunner runner, BundleLayout layout, PgDumpBackupJob dumps)
{
    public async Task<PgUpgradeResult> RunAsync(PgUpgradeRequest request, Func<CancellationToken, Task> stopOldCluster, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(stopOldCluster);

        var from = request.Old.Major;
        var to = request.ToMajor;
        RequireInitialised(layout.Resolve(from).DataDirectory, from, "old");
        RequireInitialised(layout.Resolve(to).DataDirectory, to, "new");

        var backup = await dumps.RunAsync(request.Old, request.BackupDirectory, request.KeepDumps, cancellationToken);

        await stopOldCluster(cancellationToken);
        if (await IsListeningAsync(request.Old.Port, cancellationToken))
        {
            throw new PgMaintenanceException($"Something still listens on port {request.Old.Port}. Stop the PostgreSQL {from} cluster and run the upgrade again.");
        }

        // pg_upgrade starts the old cluster for its checks and connects as the install user, so it needs the password too.
        // pg_upgrade writes its logs and temporary sockets to the working directory, so it gets a folder of its own.
        var work = Path.Combine(request.BackupDirectory, $"upg-{from}-{to}");
        Directory.CreateDirectory(work);

        var check = PgUpgradePlanner.Plan(layout, from, to, checkOnly: true, clusterRunning: false, superuser: request.Old.User);
        var checkRun = await runner.RunAsync(check.FileName, check.Arguments, cancellationToken, request.Old.Environment, work);
        if (checkRun.ExitCode != 0)
        {
            throw new PgMaintenanceException(
                $"pg_upgrade --check failed, so no data was changed. Start the PostgreSQL {from} cluster again. Output: {Trim(checkRun.Output)}");
        }

        var upgrade = PgUpgradePlanner.Plan(layout, from, to, checkOnly: false, clusterRunning: false, link: false, superuser: request.Old.User);
        var upgradeRun = await runner.RunAsync(upgrade.FileName, upgrade.Arguments, cancellationToken, request.Old.Environment, work);
        if (upgradeRun.ExitCode != 0)
        {
            throw new PgMaintenanceException(
                $"pg_upgrade failed. The PostgreSQL {from} data directory was copied, not changed. Restore point: {backup.DumpPath}. Output: {Trim(upgradeRun.Output)}");
        }

        return new PgUpgradeResult(backup, from, to);
    }

    private static void RequireInitialised(string dataDirectory, int major, string role)
    {
        var versionFile = Path.Combine(dataDirectory, "PG_VERSION");
        if (!File.Exists(versionFile))
        {
            throw new PgMaintenanceException(
                $"The {role} cluster in {dataDirectory} is missing. " +
                (role == "new" ? $"Initialise it with the PostgreSQL {major} bundle (initdb, with the old superuser password) before upgrading." : "Nothing to upgrade."));
        }
    }

    /// <summary>True when something accepts TCP connections on the loopback port within one second.</summary>
    internal static async Task<bool> IsListeningAsync(int port, CancellationToken cancellationToken)
    {
        using var client = new TcpClient();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(1));
        try
        {
            await client.ConnectAsync(System.Net.IPAddress.Loopback, port, timeout.Token);
            return true;
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException)
        {
            return false;
        }
    }

    /// <summary>pg_upgrade prints every check in order and states the failure last, so the tail is the part worth keeping.</summary>
    private static string Trim(string text) => text.Length <= 1500 ? text.Trim() : text[^1500..].Trim();
}
