using Bazaar.Server.Council.Data;
using Bazaar.Server.Data.Migrations;
using Bazaar.Server.Gateway.Data;
using Bazaar.Server.GcReactions;
using Bazaar.Server.Hive;
using Bazaar.Server.TalentPool.Component;
using Bazaar.Server.Modules;
using Bazaar.Server.Portability.Postgres;
using Bazaar.Server.Setup.Postgres;
using Bazaar.Server.Vms.Environments;

namespace Bazaar.Server.Host.Startup;

/// <summary>The migration files the host runs: the server's own, the module manager's <c>modules</c> namespace, the Gateway module's <c>gateway</c> namespace, the module component files under Sql/components/ (Vms, Council, Hive, Talent Pool, Setup), and the portability namespace (import jobs, doc 44) from the Portability.Postgres assembly.</summary>
public static class HostMigrations
{
    /// <summary>Server, module manager, Gateway, Vms, Council, Hive, Setup and Portability migrations, read from their assemblies.</summary>
    public static IMigrationSource Source() => new CompositeMigrationSource(
        new EmbeddedMigrationSource(typeof(MigrationRunner).Assembly),
        new EmbeddedMigrationSource(typeof(ModuleManagerComponent).Assembly),
        new EmbeddedMigrationSource(typeof(UsageRepository).Assembly),
        new ComponentMigrationSource(typeof(EnvironmentRepository).Assembly),
        new ComponentMigrationSource(typeof(CouncilStore).Assembly),
        new ComponentMigrationSource(typeof(HiveComponent).Assembly),
        new ComponentMigrationSource(typeof(GcReactionsComponent).Assembly),
        new ComponentMigrationSource(typeof(TalentPoolComponent).Assembly),
        new ComponentMigrationSource(typeof(PostgresApplyResultStore).Assembly),
        new EmbeddedMigrationSource(typeof(PostgresImportJobStore).Assembly));
}
