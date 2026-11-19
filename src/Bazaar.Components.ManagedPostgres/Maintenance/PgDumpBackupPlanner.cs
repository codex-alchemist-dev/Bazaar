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

    /// <summary>
    /// Restores a custom-format dump into an existing, empty database. <c>--no-owner</c> and <c>--no-privileges</c> let the
    /// dump restore under the connecting role, and <c>--exit-on-error</c> stops at the first failed statement.
    /// </summary>
    public static PgUpgradeCommand PlanRestore(BundleLayout layout, int major, string dumpFile, int port, string user, string database)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentException.ThrowIfNullOrWhiteSpace(dumpFile);
        ArgumentException.ThrowIfNullOrWhiteSpace(user);
        ArgumentException.ThrowIfNullOrWhiteSpace(database);

        var paths = layout.Resolve(major);
        return new PgUpgradeCommand(
            Path.Combine(paths.Bin, ExecutableName("pg_restore")),
            ["--no-owner", "--no-privileges", "--exit-on-error", "--no-password", "-h", "127.0.0.1", "-p", port.ToString(System.Globalization.CultureInfo.InvariantCulture), "-U", user, "-d", database, dumpFile]);
    }

    /// <summary>Creates a database. It fails when the name is taken, so a restore can never land in an existing database.</summary>
    public static PgUpgradeCommand PlanCreateDatabase(BundleLayout layout, int major, int port, string user, string database)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentException.ThrowIfNullOrWhiteSpace(user);
        ArgumentException.ThrowIfNullOrWhiteSpace(database);

        var paths = layout.Resolve(major);
        return new PgUpgradeCommand(
            Path.Combine(paths.Bin, ExecutableName("createdb")),
            ["--no-password", "-h", "127.0.0.1", "-p", port.ToString(System.Globalization.CultureInfo.InvariantCulture), "-U", user, database]);
    }

    /// <summary>Drops a database. Used only to roll back a restore that this run created.</summary>
    public static PgUpgradeCommand PlanDropDatabase(BundleLayout layout, int major, int port, string user, string database)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentException.ThrowIfNullOrWhiteSpace(user);
        ArgumentException.ThrowIfNullOrWhiteSpace(database);

        var paths = layout.Resolve(major);
        return new PgUpgradeCommand(
            Path.Combine(paths.Bin, ExecutableName("dropdb")),
            ["--no-password", "--if-exists", "-h", "127.0.0.1", "-p", port.ToString(System.Globalization.CultureInfo.InvariantCulture), "-U", user, database]);
    }

    /// <summary>Runs one SQL statement with psql, stopping on the first error. Used for checks, not for schema work.</summary>
    public static PgUpgradeCommand PlanSql(BundleLayout layout, int major, int port, string user, string database, string sql)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentException.ThrowIfNullOrWhiteSpace(user);
        ArgumentException.ThrowIfNullOrWhiteSpace(database);
        ArgumentException.ThrowIfNullOrWhiteSpace(sql);

        var paths = layout.Resolve(major);
        return new PgUpgradeCommand(
            Path.Combine(paths.Bin, ExecutableName("psql")),
            ["-X", "-q", "-A", "-t", "--no-password", "-v", "ON_ERROR_STOP=1", "-h", "127.0.0.1", "-p", port.ToString(System.Globalization.CultureInfo.InvariantCulture), "-U", user, "-d", database, "-c", sql]);
    }

    private static string ExecutableName(string tool) => OperatingSystem.IsWindows() ? tool + ".exe" : tool;
}
