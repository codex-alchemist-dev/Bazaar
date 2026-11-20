using Bazaar.Server.Agents.Turns;
using Microsoft.Extensions.DependencyInjection;

namespace Bazaar.Server.Agents.Tests.Turns.Registry;

public sealed class InProcessRunningTurnRegistryTests
{
    private static RunningTurnInfo Info(Guid? turnId = null, IReadOnlyList<string>? tools = null) => new(
        turnId ?? Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "holder-1", tools ?? ["read", "grep"], 1, 12, "gc-1", 5, "route/x");

    [Fact]
    public void A_registered_turn_is_found_with_everything_it_holds()
    {
        var registry = new InProcessRunningTurnRegistry();
        var info = Info();

        using var _ = registry.Register(info);

        var found = registry.TryGet(info.TurnId);
        Assert.NotNull(found);
        Assert.Equal(info.AgentId, found.AgentId);
        Assert.Equal(info.SessionId, found.SessionId);
        Assert.Equal("holder-1", found.LeaseHolder);
        Assert.Equal(["read", "grep"], found.ToolAllowlist);
        Assert.Equal((1, 12, "gc-1", 5L, "route/x"), (found.Depth, found.StepCap, found.TriggerGcId, found.TriggerGcSeq, found.Route));
    }

    [Fact]
    public void An_unknown_turn_id_is_a_miss()
    {
        var registry = new InProcessRunningTurnRegistry();
        using var _ = registry.Register(Info());

        Assert.Null(registry.TryGet(Guid.NewGuid()));
        Assert.Null(registry.TryGet(Guid.Empty));
    }

    [Fact]
    public void Disposing_the_registration_removes_the_turn_and_a_second_dispose_is_harmless()
    {
        var registry = new InProcessRunningTurnRegistry();
        var info = Info();
        var registration = registry.Register(info);

        registration.Dispose();
        registration.Dispose();

        Assert.Null(registry.TryGet(info.TurnId));
    }

    [Fact]
    public void A_turn_ended_by_an_exception_is_removed_when_the_scope_unwinds()
    {
        var registry = new InProcessRunningTurnRegistry();
        var info = Info();

        Action crashing = () =>
        {
            using var registration = registry.Register(info);
            Assert.NotNull(registry.TryGet(info.TurnId));
            throw new InvalidOperationException("crash");
        };
        Assert.Throws<InvalidOperationException>(crashing);

        Assert.Null(registry.TryGet(info.TurnId));
    }

    [Fact]
    public void A_late_dispose_does_not_remove_a_later_turn_with_the_same_id()
    {
        var registry = new InProcessRunningTurnRegistry();
        var id = Guid.NewGuid();
        var first = registry.Register(Info(id));
        first.Dispose();
        using var second = registry.Register(Info(id));

        first.Dispose();

        Assert.NotNull(registry.TryGet(id));
    }

    [Fact]
    public void Registering_the_same_turn_twice_is_rejected_and_keeps_the_first()
    {
        var registry = new InProcessRunningTurnRegistry();
        var first = Info();
        using var _ = registry.Register(first);

        Assert.Throws<InvalidOperationException>(() => registry.Register(first with { LeaseHolder = "other" }));

        Assert.Equal("holder-1", registry.TryGet(first.TurnId)!.LeaseHolder);
    }

    [Fact]
    public void The_stored_allowlist_is_a_read_only_copy()
    {
        var registry = new InProcessRunningTurnRegistry();
        var tools = new List<string> { "read" };
        var info = Info(tools: tools);
        using var _ = registry.Register(info);

        tools.Add("shell");

        var stored = registry.TryGet(info.TurnId)!.ToolAllowlist;
        Assert.Equal(["read"], stored);
        Assert.False(stored is List<string>);
        Assert.Throws<NotSupportedException>(() => ((IList<string>)stored).Add("shell"));
    }

    [Fact]
    public void An_empty_turn_id_or_blank_holder_is_rejected()
    {
        var registry = new InProcessRunningTurnRegistry();

        Assert.Throws<ArgumentException>(() => registry.Register(Info() with { TurnId = Guid.Empty }));
        Assert.Throws<ArgumentException>(() => registry.Register(Info() with { LeaseHolder = " " }));
    }

    [Fact]
    public void The_service_extension_shares_one_registry_between_the_read_and_write_sides()
    {
        using var provider = new ServiceCollection().AddRunningTurnRegistry().BuildServiceProvider();
        var info = Info();

        using var _ = provider.GetRequiredService<IRunningTurnRegistrar>().Register(info);

        Assert.NotNull(provider.GetRequiredService<IRunningTurnRegistry>().TryGet(info.TurnId));
    }

    [Fact]
    public void The_index_lists_only_the_agents_running_turns_and_drops_ended_ones()
    {
        var registry = new InProcessRunningTurnRegistry();
        var agent = Guid.NewGuid();
        var mine = new RunningTurnInfo(Guid.NewGuid(), agent, Guid.NewGuid(), "h", [], 0, 5, null, null, "");
        var theirs = Info();
        using var other = registry.Register(theirs);
        var registration = registry.Register(mine);

        Assert.Equal([mine.TurnId], registry.ForAgent(agent).Select(t => t.TurnId));
        Assert.Empty(registry.ForAgent(Guid.NewGuid()));

        registration.Dispose();
        Assert.Empty(registry.ForAgent(agent));
    }

    [Fact]
    public void The_index_is_registered_with_the_registry()
    {
        using var provider = new ServiceCollection().AddRunningTurnRegistry().BuildServiceProvider();

        Assert.Same(provider.GetRequiredService<InProcessRunningTurnRegistry>(), provider.GetRequiredService<IRunningTurnIndex>());
    }
}
