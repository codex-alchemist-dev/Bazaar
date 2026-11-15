using Bazaar.Server.Agents.Turns;
using Bazaar.Server.Agents.Turns.Context;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Bazaar.Server.Tests.Agents.Turns;

public class AgentTurnsRunnerRegistrationTests
{
    [Fact]
    public void Turn_runner_and_assembler_are_registered_but_not_built_until_resolved()
    {
        var services = new ServiceCollection();

        new AgentTurnsComponent().AddServices(services, new ConfigurationBuilder().Build());

        Assert.Contains(services, d => d.ServiceType == typeof(ITurnRunner));
        Assert.Contains(services, d => d.ServiceType == typeof(ITurnContextAssembler));
    }

    [Fact]
    public void Two_context_sources_are_registered_in_order_charter_then_gc()
    {
        var services = new ServiceCollection();

        new AgentTurnsComponent().AddServices(services, new ConfigurationBuilder().Build());

        var sources = services.Where(d => d.ServiceType == typeof(IContextSource)).ToList();
        Assert.Equal(2, sources.Count);
        Assert.Equal(typeof(IContextSource), sources[0].ServiceType);
    }
}
