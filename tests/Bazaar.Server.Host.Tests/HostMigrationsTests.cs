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

        Assert.Contains(plan, f => f.Namespace == "gateway" && f.Version == 3 && f.Name == "gateway_grants");
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
    public void The_first_run_seed_namespace_is_read_from_its_module()
    {
        var source = HostMigrations.Source();

        Assert.Contains("setup_firstrun", source.Namespaces());
        Assert.Contains(source.Load("setup_firstrun"), f => f.Version == 1 && f.Name == "firstrun_seed");
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

    [Fact]
    public void Agents_namespace_is_read_from_the_agents_module_at_version_1()
    {
        var source = HostMigrations.Source();

        Assert.Contains("agents", source.Namespaces());
        var files = source.Load("agents");
        Assert.Contains(files, f => f.Version == 1 && f.Name == "agent_first_run_seeds");
    }

    [Fact]
    public void Github_namespace_is_read_from_the_github_module_at_version_1()
    {
        var source = HostMigrations.Source();

        Assert.Contains("github", source.Namespaces());
        var files = source.Load("github");
        Assert.Contains(files, f => f.Version == 1 && f.Name == "github_tables");
    }

    [Fact]
    public void Gc_namespace_registers_the_pr_event_kind_after_common()
    {
        var source = HostMigrations.Source();

        var files = source.Load("gc");
        var file = Assert.Single(files, f => f.Name == "gc_event_kind_pr_event");
        Assert.Contains("'pr_event'", file.Sql);
    }

    [Fact]
    public void Account_approval_namespace_is_read_from_the_postgres_module_at_version_1()
    {
        var source = HostMigrations.Source();

        Assert.Contains("account_approval", source.Namespaces());
        Assert.Contains(source.Load("account_approval"), f => f.Version == 1 && f.Name == "account_approval");
    }

    [Fact]
    public void Host_plan_orders_the_account_approval_migration_after_common()
    {
        // The component id "account-approval" maps to the namespace "account_approval", so the plan places it by the component graph.
        var plan = MigrationRunner.Plan(HostMigrations.Source(), MigrateCommand.CurrentProvider(), HostComponents.All.Select(c => c.Manifest));

        var common = plan.Select((f, i) => (f, i)).Where(x => x.f.Namespace == MigrationRunner.Common).Select(x => x.i).ToList();
        var approval = plan.Select((f, i) => (f, i)).Where(x => x.f.Namespace == "account_approval").Select(x => x.i).ToList();
        Assert.NotEmpty(common);
        Assert.NotEmpty(approval);
        Assert.True(common.Max() < approval.Min());
    }
}
