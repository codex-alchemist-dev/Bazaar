using Bazaar.Server.Security.KillSwitch;

namespace Bazaar.Server.Tests.Security.KillSwitch;

/// <summary>Round trips for the in-memory kill switch and containment stores (the default and the test double).</summary>
public class KillSwitchStateStoreTests
{
    private static readonly DateTimeOffset _t0 = new(2026, 11, 15, 9, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Kill_switch_store_starts_empty()
    {
        var store = new InMemoryKillSwitchStateStore();

        var status = await store.LoadAsync(Ct);

        Assert.Null(status.Global);
        Assert.Empty(status.AgentStops);
    }

    [Fact]
    public async Task Kill_switch_store_round_trips_a_global_stop_and_clears_it()
    {
        var store = new InMemoryKillSwitchStateStore();
        var stop = new KillSwitchStop(KillSwitchScope.Global, null, "owner-1", "Freeze", _t0);

        await store.SetGlobalAsync(stop, Ct);
        var loaded = await store.LoadAsync(Ct);
        Assert.Equal(stop, loaded.Global);

        await store.ClearGlobalAsync(Ct);
        Assert.Null((await store.LoadAsync(Ct)).Global);
    }

    [Fact]
    public async Task Kill_switch_store_replaces_an_agent_stop_and_loads_agents_ordered_by_id()
    {
        var store = new InMemoryKillSwitchStateStore();
        await store.SetAgentStopAsync(new KillSwitchStop(KillSwitchScope.Agent, "agent-b", "owner-1", "B", _t0), Ct);
        await store.SetAgentStopAsync(new KillSwitchStop(KillSwitchScope.Agent, "agent-a", "owner-1", "first", _t0), Ct);
        var replacement = new KillSwitchStop(KillSwitchScope.Agent, "agent-a", "owner-2", "second", _t0.AddMinutes(5));

        await store.SetAgentStopAsync(replacement, Ct);
        var status = await store.LoadAsync(Ct);

        Assert.Equal(["agent-a", "agent-b"], status.AgentStops.Select(s => s.AgentId));
        Assert.Equal(replacement, status.AgentStops[0]);
    }

    [Fact]
    public async Task Kill_switch_store_clears_one_agent_stop_and_keeps_the_rest()
    {
        var store = new InMemoryKillSwitchStateStore();
        await store.SetAgentStopAsync(new KillSwitchStop(KillSwitchScope.Agent, "agent-a", "owner-1", "A", _t0), Ct);
        await store.SetAgentStopAsync(new KillSwitchStop(KillSwitchScope.Agent, "agent-b", "owner-1", "B", _t0), Ct);

        await store.ClearAgentStopAsync("agent-a", Ct);

        Assert.Equal(["agent-b"], (await store.LoadAsync(Ct)).AgentStops.Select(s => s.AgentId));
    }

    [Fact]
    public async Task Containment_store_round_trips_and_keeps_the_first_record_for_an_agent()
    {
        var store = new InMemoryContainmentStateStore();
        var first = new ContainedAgent("agent-a", _t0, "system:containment");

        await store.AddAsync(first, Ct);
        await store.AddAsync(new ContainedAgent("agent-a", _t0.AddMinutes(1), "other"), Ct);
        await store.AddAsync(new ContainedAgent("agent-b", _t0, "system:containment"), Ct);

        var loaded = await store.LoadAsync(Ct);
        Assert.Equal(["agent-a", "agent-b"], loaded.Select(a => a.AgentId));
        Assert.Equal(first, loaded[0]);
    }

    [Fact]
    public async Task Containment_store_removes_an_agent_and_ignores_one_that_is_not_stored()
    {
        var store = new InMemoryContainmentStateStore();
        await store.AddAsync(new ContainedAgent("agent-a", _t0, "system:containment"), Ct);

        await store.RemoveAsync("agent-a", Ct);
        await store.RemoveAsync("agent-missing", Ct);

        Assert.Empty(await store.LoadAsync(Ct));
    }
}
