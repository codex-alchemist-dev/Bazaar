using Bazaar.Server.Components;
using Bazaar.Server.Mail;

namespace Bazaar.Server.Tests.Mail;

public sealed class MailComponentTests
{
    [Fact]
    public void Manifest_names_the_mail_migration_namespace()
    {
        var manifest = new MailComponent().Manifest;

        Assert.Equal("mail", manifest.Id);
        Assert.Equal("mail", manifest.MigrationNamespace);
        Assert.Equal(ComponentKind.Feature, manifest.Kind);
    }

    [Fact]
    public void Component_orders_on_its_own_with_no_dependencies()
    {
        var ordered = ComponentGraph.Order([new MailComponent().Manifest]);

        Assert.Single(ordered);
        Assert.Equal("mail", ordered[0].Id);
    }
}
