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
    public void Psych_ward_namespace_is_read_from_the_psych_ward_module_at_version_1()
    {
        var source = HostMigrations.Source();

        Assert.Contains("psych_ward", source.Namespaces());
        Assert.Contains(source.Load("psych_ward"), f => f.Version == 1 && f.Name == "ward_and_vent");
    }
}
