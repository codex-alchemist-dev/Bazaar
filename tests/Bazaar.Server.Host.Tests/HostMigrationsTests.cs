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
    public void Constellation_namespace_holds_0001_from_the_server_and_0002_from_the_constellation_module()
    {
        var source = HostMigrations.Source();

        var files = source.Load("constellation");
        Assert.Contains(files, f => f.Version == 1 && f.Name == "memory_graph");
        Assert.Contains(files, f => f.Version == 2 && f.Name == "memory_doc_id");
        Assert.Equal(2, files.Count);
    }
}
