using Bazaar.Server.Data.Migrations;
using Bazaar.Server.OfflineSync.Postgres;
using Bazaar.Server.Vms.Environments;

namespace Bazaar.Server.Host.Startup;

/// <summary>The migration files the host runs: the server's own, and the Vms module's component files under Sql/components/vms.</summary>
public static class HostMigrations
{
    /// <summary>Server and Vms migrations, read from their assemblies.</summary>
    public static IMigrationSource Source() => new CompositeMigrationSource(
        new EmbeddedMigrationSource(typeof(MigrationRunner).Assembly),
        new ComponentMigrationSource(typeof(EnvironmentRepository).Assembly),
        new ComponentMigrationSource(OfflineSyncPostgresServiceCollectionExtensions.MigrationAssembly));
}
