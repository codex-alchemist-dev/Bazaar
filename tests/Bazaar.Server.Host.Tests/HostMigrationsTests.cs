using Bazaar.Server.Data.Migrations;
using Bazaar.Server.Host.Composition;
using Bazaar.Server.Host.Startup;

namespace Bazaar.Server.Host.Tests;

public sealed class HostMigrationsTests
{
    [Fact]
    public void Composed_plan_includes_the_gateway_grants_migration()
    {
        // Plan is pure (no database), so this checks that the host's composed source discovers the gateway namespace.
        var plan = MigrationRunner.Plan(HostMigrations.Source(), MigrationNamespaces.Common, HostComponents.All.Select(c => c.Manifest));

        Assert.Contains(plan, f => f.Namespace == "gateway" && f.Version == 1 && f.Name == "gateway_grants");
    }

    [Fact]
    public void Council_namespace_is_read_from_the_council_module_at_version_1()
    {
        var source = HostMigrations.Source();

        Assert.Contains("council", source.Namespaces());
        var files = source.Load("council");
        Assert.Contains(files, f => f.Version == 1 && f.Name == "council");
    }

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
