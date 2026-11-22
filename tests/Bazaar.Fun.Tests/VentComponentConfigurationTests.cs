using Bazaar.Fun;
using Bazaar.Fun.Vent;
using Cdax.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Bazaar.Fun.Tests;

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
        services.AddSingleton<IConfiguration>(configuration);
        services.AddCdaxModules(configuration, [new FunComponent()]);

        using var provider = services.BuildServiceProvider();
        var settings = provider.GetRequiredService<VentSettings>();

        Assert.False(settings.RequiredBeforeCard);
        Assert.Equal(VentSettings.InternalGeneralGc, settings.PublishTarget);
    }
}
