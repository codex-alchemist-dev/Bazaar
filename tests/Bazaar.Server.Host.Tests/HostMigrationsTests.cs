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
    public void The_first_run_seed_namespace_is_read_from_its_module()
    {
        var source = HostMigrations.Source();

        Assert.Contains("setup_firstrun", source.Namespaces());
        Assert.Contains(source.Load("setup_firstrun"), f => f.Version == 1 && f.Name == "firstrun_seed");
    }
}
