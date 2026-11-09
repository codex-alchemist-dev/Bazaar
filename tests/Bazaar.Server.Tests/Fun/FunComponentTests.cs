using Bazaar.Server.Components;
using Bazaar.Server.Fun;
using Bazaar.Server.Fun.Ward;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Bazaar.Server.Tests.Fun;

public sealed class FunComponentTests
{
    [Fact]
    public void Component_binds_ward_settings_and_registers_the_state_machine()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [$"{WardSettings.SectionName}:MinStayMinutes"] = "15",
            })
            .Build();
        var services = new ServiceCollection();
        services.AddBazaarComponents(configuration, [new FunComponent()]);

        using var provider = services.BuildServiceProvider();

        Assert.Equal(15, provider.GetRequiredService<IOptions<WardSettings>>().Value.MinStayMinutes);
        Assert.NotNull(provider.GetRequiredService<WardStateMachine>());
    }

    [Fact]
    public void Invalid_ward_settings_fail_options_validation()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [$"{WardSettings.SectionName}:MinStayMinutes"] = "-1",
            })
            .Build();
        var services = new ServiceCollection();
        services.AddBazaarComponents(configuration, [new FunComponent()]);

        using var provider = services.BuildServiceProvider();

        Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<WardStateMachine>());
    }
}
