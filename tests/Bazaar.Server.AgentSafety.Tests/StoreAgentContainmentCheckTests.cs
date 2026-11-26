using Bazaar.Server.AgentSafety.Containment;
using Bazaar.Server.Security.KillSwitch;

namespace Bazaar.Server.AgentSafety.Tests;

public sealed class StoreAgentContainmentCheckTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_stored_agent_is_contained_whatever_the_guid_text_form()
    {
        var store = new InMemoryContainmentStateStore();
        var agent = Guid.CreateVersion7();
        await store.AddAsync(new ContainedAgent(agent.ToString("N"), DateTimeOffset.UnixEpoch, "owner"), Ct);
        var check = new StoreAgentContainmentCheck(store);

        Assert.True(await check.IsContainedAsync(agent, Ct));
        Assert.False(await check.IsContainedAsync(Guid.CreateVersion7(), Ct));
    }

    [Fact]
    public async Task A_released_agent_is_no_longer_contained()
    {
        var store = new InMemoryContainmentStateStore();
        var agent = Guid.CreateVersion7();
        await store.AddAsync(new ContainedAgent(agent.ToString(), DateTimeOffset.UnixEpoch, "owner"), Ct);
        await store.RemoveAsync(agent.ToString(), Ct);

        Assert.False(await new StoreAgentContainmentCheck(store).IsContainedAsync(agent, Ct));
    }
}
