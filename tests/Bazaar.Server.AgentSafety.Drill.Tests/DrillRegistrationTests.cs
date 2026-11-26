using Bazaar.Server.Security.KillSwitch;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Time.Testing;

namespace Bazaar.Server.AgentSafety.Drill.Tests;

public sealed class DrillRegistrationTests
{
    [Fact]
    public async Task The_registration_resolves_the_runner_status_and_scheduler()
    {
        var h = new DrillHarness(new FakeTimeProvider());
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(h.Engine);
        services.AddSingleton<IAgentContainment>(h.Containment);
        services.AddSingleton<IDrillProbe>(h.Containment);
        services.AddSingleton<IDrillTarget>(h.Target);
        services.AddBazaarKillSwitchDrill();

        await using var provider = services.BuildServiceProvider();

        Assert.NotNull(provider.GetRequiredService<DrillRunner>());
        Assert.IsType<DrillStatus>(provider.GetRequiredService<IDrillStatus>());
        Assert.IsType<InMemoryDrillResultStore>(provider.GetRequiredService<IDrillResultStore>());
        Assert.Contains(provider.GetServices<IHostedService>(), s => s is DrillScheduler);
    }

    [Fact]
    public void A_store_registered_before_the_drill_is_kept()
    {
        var durable = new InMemoryDrillResultStore();
        var services = new ServiceCollection();
        services.AddSingleton<IDrillResultStore>(durable);
        services.AddBazaarKillSwitchDrill();

        using var provider = services.BuildServiceProvider();

        Assert.Same(durable, provider.GetRequiredService<IDrillResultStore>());
    }
}
