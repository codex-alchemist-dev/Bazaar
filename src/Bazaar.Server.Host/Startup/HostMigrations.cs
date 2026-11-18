using Bazaar.Server.Data.Migrations;
using Bazaar.Server.Hive;
using Bazaar.Server.Vms.Environments;

namespace Bazaar.Server.Host.Startup;

/// <summary>The migration files the host runs: the server's own, and the Vms and Hive modules' component files under Sql/components/{id}.</summary>
public static class HostMigrations
{
    /// <summary>Server, Vms and Hive migrations, read from their assemblies.</summary>
    public static IMigrationSource Source() => new CompositeMigrationSource(
        new EmbeddedMigrationSource(typeof(MigrationRunner).Assembly),
        new ComponentMigrationSource(typeof(EnvironmentRepository).Assembly),
        new ComponentMigrationSource(typeof(HiveComponent).Assembly));
}
