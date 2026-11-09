using Bazaar.Server.Components;
using Bazaar.Server.Fun;
using Bazaar.Server.Fun.Vent;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Bazaar.Server.Tests.Fun;

public sealed class VentComponentConfigurationTests
{
    [Fact]
    public void Component_binds_vent_settings()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [$"{VentSettings.SectionName}:RequiredBeforeCard"] = "false",
                [$"{VentSettings.SectionName}:PublishTarget"] = "internal_general_gc",
            })
            .Build();
        var services = new ServiceCollection();
        services.AddBazaarComponents(configuration, [new FunComponent()]);

        using var provider = services.BuildServiceProvider();
        var settings = provider.GetRequiredService<VentSettings>();

        Assert.False(settings.RequiredBeforeCard);
        Assert.Equal(VentSettings.InternalGeneralGc, settings.PublishTarget);
    }
}
