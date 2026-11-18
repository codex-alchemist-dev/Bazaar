using Bazaar.Server.Data.Migrations;
using Bazaar.Server.Portability.Postgres;
using Bazaar.Server.Vms.Environments;

namespace Bazaar.Server.Host.Startup;

/// <summary>
/// The migration files the host runs: the server's own, the Vms module's component files under Sql/components/vms, and the
/// portability namespace (import jobs, doc 44) from the Portability.Postgres assembly.
/// </summary>
public static class HostMigrations
{
    /// <summary>Server, Vms and Portability migrations, read from their assemblies.</summary>
    public static IMigrationSource Source() => new CompositeMigrationSource(
        new EmbeddedMigrationSource(typeof(MigrationRunner).Assembly),
        new ComponentMigrationSource(typeof(EnvironmentRepository).Assembly),
        new EmbeddedMigrationSource(typeof(PostgresImportJobStore).Assembly));
}
