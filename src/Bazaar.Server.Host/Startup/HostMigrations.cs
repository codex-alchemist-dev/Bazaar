using Bazaar.Server.Data.Migrations;
using Bazaar.Server.Hive;
using Bazaar.Server.TalentPool;
using Bazaar.Server.Vms.Environments;

namespace Bazaar.Server.Host.Startup;

/// <summary>The migration files the host runs: the server's own, the Vms and Hive modules' component files under Sql/components/{id}, and the Talent Pool module's Sql/talent_pool namespace.</summary>
public static class HostMigrations
{
    /// <summary>Server, Vms, Hive and Talent Pool migrations, read from their assemblies.</summary>
    public static IMigrationSource Source() => new CompositeMigrationSource(
        new EmbeddedMigrationSource(typeof(MigrationRunner).Assembly),
        new ComponentMigrationSource(typeof(EnvironmentRepository).Assembly),
        new ComponentMigrationSource(typeof(HiveRegistration).Assembly),
        new EmbeddedMigrationSource(typeof(TalentPoolServiceCollectionExtensions).Assembly));
}
