using Bazaar.Server.Agents;
using Bazaar.Server.Agents.Data;

namespace Bazaar.Server.Tests.Agents.FirstRun;

public class InMemoryAgentFirstRunSeedsTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Held_agent_stays_in_created_when_the_automatic_step_runs()
    {
        var seeds = new InMemoryAgentFirstRunSeeds();
        var id = await seeds.CreateHeldAsync("Charles", "seed:charles", ct: Ct);

        Assert.Equal(AgentState.Created, seeds.StateOf(id));
        Assert.True(seeds.IsHeld(id));
        Assert.False(await seeds.TryStartAutomaticOnboardingAsync(id, Ct));
        Assert.Equal(AgentState.Created, seeds.StateOf(id));
    }

    [Fact]
    public async Task Releasing_the_hold_lets_the_automatic_step_move_the_agent_to_onboarding()
    {
        var seeds = new InMemoryAgentFirstRunSeeds();
        var id = await seeds.CreateHeldAsync("Charles", "seed:charles", ct: Ct);

        Assert.True(await seeds.ReleaseHoldAsync(id, Ct));
        Assert.False(seeds.IsHeld(id));
        Assert.True(await seeds.TryStartAutomaticOnboardingAsync(id, Ct));
        Assert.Equal(AgentState.Onboarding, seeds.StateOf(id));
    }

    [Fact]
    public async Task Releasing_twice_or_an_unknown_agent_reports_no_change()
    {
        var seeds = new InMemoryAgentFirstRunSeeds();
        var id = await seeds.CreateHeldAsync("Charles", "seed:charles", ct: Ct);

        Assert.True(await seeds.ReleaseHoldAsync(id, Ct));
        Assert.False(await seeds.ReleaseHoldAsync(id, Ct));
        Assert.False(await seeds.ReleaseHoldAsync(Guid.CreateVersion7(), Ct));
    }

    [Fact]
    public async Task Automatic_step_does_not_fire_for_an_agent_that_is_not_in_created()
    {
        var seeds = new InMemoryAgentFirstRunSeeds();
        var id = await seeds.CreateHeldAsync("Charles", "seed:charles", ct: Ct);
        await seeds.ReleaseHoldAsync(id, Ct);
        Assert.True(await seeds.TryStartAutomaticOnboardingAsync(id, Ct));

        Assert.False(await seeds.TryStartAutomaticOnboardingAsync(id, Ct));
        Assert.False(await seeds.TryStartAutomaticOnboardingAsync(Guid.CreateVersion7(), Ct));
        Assert.Equal(AgentState.Onboarding, seeds.StateOf(id));
    }

    [Fact]
    public async Task Marker_lookup_finds_the_agent_and_is_idempotent()
    {
        var seeds = new InMemoryAgentFirstRunSeeds();
        var id = await seeds.CreateHeldAsync("Charles", "seed:charles", ct: Ct);

        Assert.Equal(id, await seeds.FindByMarkerAsync("seed:charles", Ct));
        Assert.Equal(id, await seeds.FindByMarkerAsync("seed:charles", Ct));
        Assert.Null(await seeds.FindByMarkerAsync("seed:someone-else", Ct));
    }

    [Fact]
    public async Task Creating_twice_with_one_marker_is_refused_and_leaves_one_agent()
    {
        var seeds = new InMemoryAgentFirstRunSeeds();
        var id = await seeds.CreateHeldAsync("Charles", "seed:charles", ct: Ct);

        await Assert.ThrowsAsync<InvalidOperationException>(() => seeds.CreateHeldAsync("Charles", "seed:charles", ct: Ct));
        Assert.Equal(id, await seeds.FindByMarkerAsync("seed:charles", Ct));
    }

    [Fact]
    public async Task Remove_unstarted_deletes_an_agent_that_never_started_a_turn()
    {
        var seeds = new InMemoryAgentFirstRunSeeds();
        var id = await seeds.CreateHeldAsync("Charles", "seed:charles", ct: Ct);

        Assert.True(await seeds.RemoveUnstartedAsync(id, Ct));
        Assert.Null(seeds.StateOf(id));
        Assert.Null(await seeds.FindByMarkerAsync("seed:charles", Ct));
        Assert.False(await seeds.RemoveUnstartedAsync(id, Ct));
    }

    [Fact]
    public async Task Remove_unstarted_refuses_an_agent_that_started_a_turn()
    {
        var seeds = new InMemoryAgentFirstRunSeeds();
        var id = await seeds.CreateHeldAsync("Charles", "seed:charles", ct: Ct);
        await seeds.ReleaseHoldAsync(id, Ct);
        seeds.MarkTurnStarted(id);

        Assert.False(await seeds.RemoveUnstartedAsync(id, Ct));
        Assert.Equal(AgentState.Created, seeds.StateOf(id));
        Assert.Equal(id, await seeds.FindByMarkerAsync("seed:charles", Ct));
    }
}
