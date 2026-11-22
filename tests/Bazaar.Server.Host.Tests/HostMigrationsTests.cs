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
    public void Agents_namespace_is_read_from_the_agents_module_at_version_1()
    {
        var source = HostMigrations.Source();

        Assert.Contains("agents", source.Namespaces());
        var files = source.Load("agents");
        Assert.Contains(files, f => f.Version == 1 && f.Name == "agent_first_run_seeds");
    }
}
