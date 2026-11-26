using System.Text.Json;
using Bazaar.RyuseiFuda.Kanban;
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
    public async Task Carries_the_agent_role_into_the_entry_and_the_route_json()
    {
        var directory = new AgentOrgDirectory(new FakeAgents([new AgentListing(_alice, "Alice", "Engineer"), new AgentListing(_bob, "Bob")]));

        var agents = await directory.ListAgentsAsync(TestContext.Current.CancellationToken);
        var json = JsonElement.Parse(OrgReadRoute.WriteModel([], agents, []));

        var rows = json.GetProperty("agents").EnumerateArray().ToArray();
        Assert.Equal("Engineer", rows[0].GetProperty("role").GetString());
        Assert.Equal(JsonValueKind.Null, rows[1].GetProperty("role").ValueKind);
    }

    [Fact]
    public void Project_entries_carry_no_role_field()
    {
        var json = JsonElement.Parse(OrgReadRoute.WriteModel([new OrgDirectoryEntry(_alice, "Board")], [], []));

        Assert.False(json.GetProperty("projects")[0].TryGetProperty("role", out _));
    }

    [Fact]
    public async Task Lists_no_projects_when_no_board_reader_is_registered()
    {
        var directory = new AgentOrgDirectory(new FakeAgents([new AgentListing(_alice, "Alice")]));

        Assert.Empty(await directory.ListProjectsAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Lists_boards_as_projects_and_the_route_json_carries_their_names()
    {
        var directory = new AgentOrgDirectory(new FakeAgents([]), new FakeBoards([new BoardListing(_alice, "Bazaar"), new BoardListing(_bob, "Project 00000000")]));

        var projects = await directory.ListProjectsAsync(TestContext.Current.CancellationToken);
        var json = JsonElement.Parse(OrgReadRoute.WriteModel(projects, [], []));

        Assert.Equal([new OrgDirectoryEntry(_alice, "Bazaar"), new OrgDirectoryEntry(_bob, "Project 00000000")], projects);
        var rows = json.GetProperty("projects").EnumerateArray().ToArray();
        Assert.Equal("Bazaar", rows[0].GetProperty("name").GetString());
        Assert.Equal(_alice.ToString(), rows[0].GetProperty("id").GetString());
    }

    [Fact]
    public async Task Component_maps_a_registered_board_reader_into_the_directory()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IAgentDirectoryReader>(new FakeAgents([]));
        services.AddSingleton<IBoardDirectoryReader>(new FakeBoards([new BoardListing(_bob, "Bazaar")]));
        new OrgComponent().AddServices(services, new ConfigurationBuilder().Build());
        await using var provider = services.BuildServiceProvider();

        var projects = await provider.GetRequiredService<IOrgDirectory>().ListProjectsAsync(TestContext.Current.CancellationToken);

        Assert.Equal([new OrgDirectoryEntry(_bob, "Bazaar")], projects);
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

    private sealed class FakeBoards(IReadOnlyList<BoardListing> rows) : IBoardDirectoryReader
    {
        public Task<IReadOnlyList<BoardListing>> ListBoardsAsync(CancellationToken ct = default) => Task.FromResult(rows);
    }

    private sealed class FakeAgents(IReadOnlyList<AgentListing> rows) : IAgentDirectoryReader
    {
        public Task<IReadOnlyList<AgentListing>> ListActiveAgentsAsync(CancellationToken ct = default) => Task.FromResult(rows);
    }
}
