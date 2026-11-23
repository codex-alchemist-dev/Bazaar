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

        Assert.Contains(plan, f => f.Namespace == "gateway" && f.Version == 5 && f.Name == "gateway_grants");
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
        var file = Assert.Single(source.Load("portability"), f => f.Version == 1);
        Assert.Equal("import_job", file.Name);
    }

    [Fact]
    public void Presence_agent_activity_migration_is_in_the_host_source()
    {
        var source = HostMigrations.Source();

        Assert.Contains("presence", source.Namespaces());
        Assert.Contains(source.Load("presence"), f => f.Version == 1 && f.Name == "agent_activity");
    }

    [Fact]
    public void Host_plan_orders_the_portability_migration_after_common()
    {
        var plan = MigrationRunner.Plan(HostMigrations.Source(), MigrateCommand.CurrentProvider(), []);

        var common = plan.Select((f, i) => (f, i)).Where(x => x.f.Namespace == MigrationRunner.Common).Select(x => x.i).ToList();
        var portability = plan.Select((f, i) => (f, i)).First(x => x.f.Namespace == "portability").i;
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
        // The component id "account-approval" maps to the namespace "account_approval". Order is the planner's namespace ordering.
        var ordered = MigrationNamespaces.Order(HostMigrations.Source().Namespaces(), HostComponents.All.Select(c => c.Manifest));

        Assert.Equal(MigrationRunner.Common, ordered[0]);
        Assert.Contains("account_approval", ordered);
        Assert.True(ordered.ToList().IndexOf("account_approval") > 0);
    }

    [Fact]
    public void Mail_namespace_is_read_from_the_mail_module_at_version_21()
    {
        var source = HostMigrations.Source();
        Assert.Contains("mail", source.Namespaces());
        Assert.Contains(source.Load("mail"), f => f.Version == 21 && f.Name == "mail_core");
    }

    [Fact]
    public void Mail_namespace_adds_the_quarantine_columns_at_version_22()
    {
        var files = HostMigrations.Source().Load("mail");

        Assert.Contains(files, f => f.Version == 22 && f.Name == "mail_quarantine");
        Assert.Equal(1, files.Count(f => f.Version == 21));
    }
}
