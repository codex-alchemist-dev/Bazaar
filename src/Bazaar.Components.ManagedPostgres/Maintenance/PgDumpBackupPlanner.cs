using Bazaar.Components.ManagedPostgres.Bundle;

namespace Bazaar.Components.ManagedPostgres.Maintenance;

/// <summary>
/// Builds the pg_dump and pg_restore invocations for the backup job. The dump uses the custom format (<c>-Fc</c>),
/// which pg_restore can restore selectively and verify. Credentials are never on the command line: the caller
/// supplies them through the environment or a password file the runner already protects.
/// </summary>
public static class PgDumpBackupPlanner
{
    public static PgUpgradeCommand PlanDump(BundleLayout layout, int major, string outputFile, int port, string user, string database)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputFile);
        ArgumentException.ThrowIfNullOrWhiteSpace(user);
        ArgumentException.ThrowIfNullOrWhiteSpace(database);

        var paths = layout.Resolve(major);
        return new PgUpgradeCommand(
            Path.Combine(paths.Bin, ExecutableName("pg_dump")),
            ["-Fc", "--no-password", "-h", "127.0.0.1", "-p", port.ToString(System.Globalization.CultureInfo.InvariantCulture), "-U", user, "-f", outputFile, database]);
    }

    public static PgUpgradeCommand PlanRestoreCheck(BundleLayout layout, int major, string dumpFile)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentException.ThrowIfNullOrWhiteSpace(dumpFile);

        var paths = layout.Resolve(major);
        return new PgUpgradeCommand(
            Path.Combine(paths.Bin, ExecutableName("pg_restore")),
            ["--list", dumpFile]);
    }

    private static string ExecutableName(string tool) => OperatingSystem.IsWindows() ? tool + ".exe" : tool;
}
