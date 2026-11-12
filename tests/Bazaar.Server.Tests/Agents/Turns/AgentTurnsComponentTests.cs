using Bazaar.Server.Agents.Data;
using Bazaar.Server.Agents.Turns;
using Bazaar.Server.Components;
using Bazaar.Server.Mentions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Bazaar.Server.Tests.Agents.Turns;

public sealed class AgentTurnsComponentTests
{
    [Fact]
    public async Task Component_registers_the_agent_repository_without_connecting()
    {
        using var dataSource = NpgsqlDataSource.Create("Host=127.0.0.1;Port=1;Database=bazaar");
        var services = new ServiceCollection();
        services.AddSingleton(dataSource);

        services.AddBazaarComponents(new ConfigurationBuilder().Build(), [new AgentTurnsComponent()]);

        await using var provider = services.BuildServiceProvider();
        Assert.NotNull(provider.GetRequiredService<AgentRepository>());
    }

    [Fact]
    public async Task Component_registers_the_handle_store_as_the_agent_handle_assigner()
    {
        using var dataSource = NpgsqlDataSource.Create("Host=127.0.0.1;Port=1;Database=bazaar");
        var services = new ServiceCollection();
        services.AddSingleton(dataSource);

        services.AddBazaarComponents(new ConfigurationBuilder().Build(), [new AgentTurnsComponent()]);

        await using var provider = services.BuildServiceProvider();
        var assigner = provider.GetRequiredService<IAgentHandleAssigner>();
        Assert.IsType<PrincipalHandleRepository>(assigner);
        Assert.Same(provider.GetRequiredService<PrincipalHandleRepository>(), assigner);
    }
}
