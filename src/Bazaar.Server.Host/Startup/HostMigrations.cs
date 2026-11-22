using Bazaar.Server.Council.Data;
using Bazaar.Server.Data.Migrations;
using Bazaar.Server.Hive;
using Bazaar.Server.Modules;
using Bazaar.Server.Setup.Postgres;
using Bazaar.Server.Vms.Environments;

namespace Bazaar.Server.Host.Startup;

/// <summary>The migration files the host runs: the server's own, the module manager's <c>modules</c> namespace, the Vms, Hive and Council modules' component files under Sql/components/{id}.</summary>
public static class HostMigrations
{
    /// <summary>Server, module manager, Vms, Council, Hive and Setup migrations, read from their assemblies.</summary>
    public static IMigrationSource Source() => new CompositeMigrationSource(
        new EmbeddedMigrationSource(typeof(MigrationRunner).Assembly),
        new EmbeddedMigrationSource(typeof(ModuleManagerComponent).Assembly),
        new ComponentMigrationSource(typeof(EnvironmentRepository).Assembly),
        new ComponentMigrationSource(typeof(CouncilStore).Assembly),
        new ComponentMigrationSource(typeof(HiveComponent).Assembly),
        new ComponentMigrationSource(typeof(PostgresApplyResultStore).Assembly));
}
