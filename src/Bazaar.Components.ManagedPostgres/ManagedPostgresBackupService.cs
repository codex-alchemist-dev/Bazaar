using Bazaar.Components.ManagedPostgres.Bundle;
using Bazaar.Components.ManagedPostgres.Maintenance;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Bazaar.Components.ManagedPostgres;

/// <summary>
/// Takes a verified logical dump of the managed cluster on a schedule. Each run is a no-op until the cluster is up, and a
/// failed run is logged and retried on the next tick, so one bad disk does not stop the host.
/// </summary>
public sealed partial class ManagedPostgresBackupService(
    IOptions<ManagedPostgresOptions> options,
    IPostgresCommandRunner runner,
    ManagedPostgresEndpoint endpoint,
    TimeProvider time,
    ILogger<ManagedPostgresBackupService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = options.Value;
        if (settings.Mode != DatabaseMode.Managed || !settings.Backup.Enabled)
        {
            return;
        }

        using var timer = new PeriodicTimer(settings.Backup.Interval, time);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                var backup = await BackupOnceAsync(stoppingToken);
                if (backup is not null)
                {
                    LogDumpTaken(logger, backup.DumpPath, backup.Bytes);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                LogDumpFailed(logger, ex);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Took PostgreSQL dump {DumpPath} ({Bytes} bytes).")]
    private static partial void LogDumpTaken(ILogger logger, string dumpPath, long bytes);

    [LoggerMessage(Level = LogLevel.Error, Message = "Scheduled PostgreSQL dump failed; it will be retried at the next interval.")]
    private static partial void LogDumpFailed(ILogger logger, Exception exception);

    /// <summary>Takes one dump now. Returns null when the cluster is not running yet or the mode is not Managed.</summary>
    public async Task<PgDumpBackup?> BackupOnceAsync(CancellationToken cancellationToken)
    {
        var settings = options.Value;
        if (settings.Mode != DatabaseMode.Managed || endpoint.Current is not { } running)
        {
            return null;
        }

        var layout = new BundleLayout(settings.BundleRoot, settings.DataRoot);
        var directory = BackupDirectory(settings);
        var job = new PgDumpBackupJob(runner, layout, time);
        return await job.RunAsync(ToConnection(running, settings.Major), directory, settings.Backup.KeepDumps, cancellationToken);
    }

    /// <summary>Where dumps are written: the configured folder, or <c>DataRoot/backups</c>.</summary>
    public static string BackupDirectory(ManagedPostgresOptions settings) =>
        string.IsNullOrWhiteSpace(settings.Backup.Directory)
            ? Path.Combine(settings.DataRoot, "backups")
            : settings.Backup.Directory;

    /// <summary>The dump job's view of the running cluster. Dumps always go over loopback, as the planner expects.</summary>
    public static PgConnection ToConnection(ManagedPostgresConnectionInfo running, int major) =>
        new(major, running.Port, running.Username, running.Password, running.Database);
}
