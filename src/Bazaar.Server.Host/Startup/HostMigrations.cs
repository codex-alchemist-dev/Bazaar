using Bazaar.Server.Data.Migrations;
using Bazaar.Server.Setup.Postgres;
using Bazaar.Server.Vms.Environments;

namespace Bazaar.Server.Host.Startup;

/// <summary>The migration files the host runs: the server's own, and the component files of the Vms and Setup modules under Sql/components.</summary>
public static class HostMigrations
{
    /// <summary>Server, Vms and Setup migrations, read from their assemblies.</summary>
    public static IMigrationSource Source() => new CompositeMigrationSource(
        new EmbeddedMigrationSource(typeof(MigrationRunner).Assembly),
        new ComponentMigrationSource(typeof(EnvironmentRepository).Assembly),
        new ComponentMigrationSource(typeof(PostgresApplyResultStore).Assembly));
}
