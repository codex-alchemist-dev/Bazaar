using Bazaar.Server.Host.Startup;
using Xunit;

namespace Bazaar.Server.Host.Tests;

public sealed class HostMigrationSourceTests
{
    [Theory]
    [InlineData("common")]
    [InlineData("vms")]
    [InlineData("hive")]
    [InlineData("talent_pool")]
    public void Host_source_includes_each_module_namespace(string ns)
    {
        var source = HostMigrations.Source();

        Assert.Contains(ns, source.Namespaces());
        Assert.NotEmpty(source.Load(ns));
    }
}
