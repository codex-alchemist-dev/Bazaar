using Bazaar.Server.Data.Migrations;
using Bazaar.Server.Diagnostics.Persistence;
using Bazaar.Server.Vms.Environments;

namespace Bazaar.Server.Host.Startup;

/// <summary>The migration files the host runs: the server's own, the Vms module's component files under Sql/components/vms, and the crash diagnostics module's Sql/diagnostics.</summary>
public static class HostMigrations
{
    /// <summary>Server and Vms migrations, read from their assemblies.</summary>
    public static IMigrationSource Source() => new CompositeMigrationSource(
        new EmbeddedMigrationSource(typeof(MigrationRunner).Assembly),
        new ComponentMigrationSource(typeof(EnvironmentRepository).Assembly),
        new EmbeddedMigrationSource(typeof(PostgresCrashStore).Assembly));
}
