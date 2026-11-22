using System.Text.Json;
using Bazaar.Server.Agents.Data;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Bazaar.Server.Org.Tests;

public sealed class AgentOrgDirectoryTests
{
    private static readonly Guid _alice = Guid.Parse("00000000-0000-7000-8000-0000000000a1");
    private static readonly Guid _bob = Guid.Parse("00000000-0000-7000-8000-0000000000b2");

    [Fact]
    public async Task Lists_agent_names_from_the_agents_reader()
    {
        var directory = new AgentOrgDirectory(new FakeAgents([new AgentListing(_alice, "Alice"), new AgentListing(_bob, "Bob")]));

        var agents = await directory.ListAgentsAsync(TestContext.Current.CancellationToken);

        Assert.Equal([new OrgDirectoryEntry(_alice, "Alice"), new OrgDirectoryEntry(_bob, "Bob")], agents);
    }

    [Fact]
    public async Task Lists_no_projects_while_no_project_registry_exists()
    {
        var directory = new AgentOrgDirectory(new FakeAgents([new AgentListing(_alice, "Alice")]));

        Assert.Empty(await directory.ListProjectsAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Component_registers_the_directory_and_the_model_carries_agent_names()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IAgentDirectoryReader>(new FakeAgents([new AgentListing(_alice, "Alice")]));
        new OrgComponent().AddServices(services, new ConfigurationBuilder().Build());
        await using var provider = services.BuildServiceProvider();

        var directory = provider.GetRequiredService<IOrgDirectory>();
        var model = JsonElement.Parse(OrgReadRoute.WriteModel(
            await directory.ListProjectsAsync(TestContext.Current.CancellationToken),
            await directory.ListAgentsAsync(TestContext.Current.CancellationToken),
            []));

        var agent = Assert.Single(model.GetProperty("agents").EnumerateArray());
        Assert.Equal(_alice.ToString(), agent.GetProperty("id").GetString());
        Assert.Equal("Alice", agent.GetProperty("name").GetString());
        Assert.Empty(model.GetProperty("projects").EnumerateArray());
    }

    private sealed class FakeAgents(IReadOnlyList<AgentListing> rows) : IAgentDirectoryReader
    {
        public Task<IReadOnlyList<AgentListing>> ListActiveAgentsAsync(CancellationToken ct = default) => Task.FromResult(rows);
    }
}
