using Bazaar.Components.ManagedPostgres.Bundle;

namespace Bazaar.Components.ManagedPostgres.Maintenance;

/// <summary>
/// Restores a logical dump into a new database (doc 33 restore rule 1: never in place). <c>createdb</c> runs first and
/// fails when the name is taken, so an existing database is never written to. If the dump has a <c>.sha256</c> sidecar,
/// the file is verified before anything is created. On a failed restore the new database is dropped again.
/// </summary>
public static class PgDumpRestore
{
    public static async Task RunAsync(
        IPostgresCommandRunner runner,
        BundleLayout layout,
        PgConnection connection,
        string dumpPath,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentException.ThrowIfNullOrWhiteSpace(dumpPath);

        if (!File.Exists(dumpPath))
        {
            throw new PgMaintenanceException("The dump file does not exist.");
        }

        var sidecar = dumpPath + PgDumpBackupJob.ChecksumExtension;
        if (File.Exists(sidecar))
        {
            var expected = (await File.ReadAllTextAsync(sidecar, cancellationToken)).Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
            var actual = await PgDumpBackupJob.HashAsync(dumpPath, cancellationToken);
            if (!string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase))
            {
                throw new PgMaintenanceException("The dump does not match its SHA-256 sidecar. Nothing was restored.");
            }
        }

        var create = PgDumpBackupPlanner.PlanCreateDatabase(layout, connection.Major, connection.Port, connection.User, connection.Database);
        var created = await runner.RunAsync(create.FileName, create.Arguments, cancellationToken, connection.Environment);
        if (created.ExitCode != 0)
        {
            throw new PgMaintenanceException($"createdb failed (the database may already exist): {Trim(created.Output)}");
        }

        try
        {
            var restore = PgDumpBackupPlanner.PlanRestore(layout, connection.Major, dumpPath, connection.Port, connection.User, connection.Database);
            var restored = await runner.RunAsync(restore.FileName, restore.Arguments, cancellationToken, connection.Environment);
            if (restored.ExitCode != 0)
            {
                throw new PgMaintenanceException($"pg_restore exited with code {restored.ExitCode}: {Trim(restored.Output)}");
            }
        }
        catch
        {
            var drop = PgDumpBackupPlanner.PlanDropDatabase(layout, connection.Major, connection.Port, connection.User, connection.Database);
            await runner.RunAsync(drop.FileName, drop.Arguments, CancellationToken.None, connection.Environment);
            throw;
        }
    }

    private static string Trim(string text) => text.Length <= 500 ? text.Trim() : text[..500].Trim();
}
