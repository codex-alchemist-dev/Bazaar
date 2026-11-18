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
    public void Mail_namespace_adds_the_quarantine_columns_at_version_22()
    {
        var files = HostMigrations.Source().Load("mail");

        Assert.Contains(files, f => f.Version == 22 && f.Name == "mail_quarantine");
        Assert.Equal(1, files.Count(f => f.Version == 21));
    }
}
