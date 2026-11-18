using Bazaar.Server.Data.Migrations;
using Bazaar.Server.Modules;
using Bazaar.Server.Vms.Environments;

namespace Bazaar.Server.Host.Startup;

/// <summary>The migration files the host runs: the server's own, the module manager's <c>modules</c> namespace, and the Vms module's component files under Sql/components/vms.</summary>
public static class HostMigrations
{
    /// <summary>Server and Vms migrations, read from their assemblies.</summary>
    public static IMigrationSource Source() => new CompositeMigrationSource(
        new EmbeddedMigrationSource(typeof(MigrationRunner).Assembly),
        new EmbeddedMigrationSource(typeof(ModuleManagerComponent).Assembly),
        new ComponentMigrationSource(typeof(EnvironmentRepository).Assembly));
}
