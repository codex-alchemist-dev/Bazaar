using Bazaar.Server.Data.Migrations;
using Bazaar.Server.Host.Startup;

namespace Bazaar.Server.Host.Tests;

public sealed class HostMigrationsTests
{
    [Fact]
    public void Council_namespace_is_read_from_the_council_module_at_version_1()
    {
        var source = HostMigrations.Source();

        Assert.Contains("council", source.Namespaces());
        var files = source.Load("council");
        Assert.Contains(files, f => f.Version == 1 && f.Name == "council");
    }

    [Fact]
    public void Mail_namespace_is_read_from_the_mail_module_at_version_21()
    {
        var source = HostMigrations.Source();

        Assert.Contains("mail", source.Namespaces());
        var files = source.Load("mail");
        Assert.Contains(files, f => f.Version == 21 && f.Name == "mail_core");
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
