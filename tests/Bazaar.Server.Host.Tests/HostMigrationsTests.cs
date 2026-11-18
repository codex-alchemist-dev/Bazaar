using Bazaar.Server.Data.Migrations;
using Bazaar.Server.Host.Startup;

namespace Bazaar.Server.Host.Tests;

/// <summary>
/// The host's migration source must include every module's embedded SQL. A module whose assembly is not read here never
/// migrates on a real install, so these checks read the source the host runs and need no database.
/// </summary>
public class HostMigrationsTests
{
    [Fact]
    public void Portability_import_job_migration_is_in_the_host_source()
    {
        var source = HostMigrations.Source();

        Assert.Contains("portability", source.Namespaces());
        var file = Assert.Single(source.Load("portability"));
        Assert.Equal(1, file.Version);
        Assert.Equal("import_job", file.Name);
    }

    [Fact]
    public void Host_plan_orders_the_portability_migration_after_common()
    {
        var plan = MigrationRunner.Plan(HostMigrations.Source(), MigrateCommand.CurrentProvider(), []);

        var common = plan.Select((f, i) => (f, i)).Where(x => x.f.Namespace == MigrationRunner.Common).Select(x => x.i).ToList();
        var portability = plan.Select((f, i) => (f, i)).Single(x => x.f.Namespace == "portability").i;
        Assert.NotEmpty(common);
        Assert.True(common.Max() < portability);
    }
}
