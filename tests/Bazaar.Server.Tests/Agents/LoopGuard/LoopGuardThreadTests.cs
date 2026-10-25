using Bazaar.Server.Agents.LoopGuard;
using Bazaar.Testing;

namespace Bazaar.Server.Tests.Agents.LoopGuard;

public class LoopGuardThreadTests
{
    private static readonly Guid _gc = Guid.Parse("0196a6f0-0000-7000-8000-000000000201");
    private static readonly Guid _otherGc = Guid.Parse("0196a6f0-0000-7000-8000-000000000202");
    private const string _a = "agent-a";
    private const string _b = "agent-b";
    private const string _c = "agent-c";

    private static LoopGuardFacade Guard(LoopGuardOptions? options = null) => new(new FakeClock(), options);

    private static GuardVerdict Say(ILoopGuard guard, string agent, int depth = 0, Guid? gc = null) =>
        guard.Check(new AgentMessage(gc ?? _gc, agent, depth));

    private static void Alternate(ILoopGuard guard, int count, params string[] pair)
    {
        for (var i = 0; i < count; i++)
        {
            Assert.False(Say(guard, pair[i % 2]).Tripped);
        }
    }

    [Fact]
    public void Ping_pong_trips_on_the_message_after_the_limit()
    {
        var guard = Guard();
        Alternate(guard, 6, _a, _b);

        var verdict = Say(guard, _a);

        Assert.True(verdict.Tripped);
        Assert.Equal(GuardKind.PingPong, verdict.Kind);
        Assert.Equal([_a, _b], verdict.AgentIds);
    }

    [Fact]
    public void Ping_pong_at_the_limit_does_not_trip()
    {
        var guard = Guard();

        Alternate(guard, 6, _a, _b);
    }

    [Fact]
    public void Human_message_resets_ping_pong()
    {
        var guard = Guard();
        Alternate(guard, 6, _a, _b);

        Assert.False(guard.Check(new HumanMessage(_gc)).Tripped);

        Alternate(guard, 6, _a, _b);
    }

    [Fact]
    public void Tool_result_resets_ping_pong()
    {
        var guard = Guard();
        Alternate(guard, 6, _a, _b);

        Assert.False(guard.Check(new ToolResult(_gc)).Tripped);

        Alternate(guard, 6, _a, _b);
    }

    [Fact]
    public void Third_agent_message_resets_ping_pong()
    {
        var guard = Guard();
        Alternate(guard, 6, _a, _b);

        Assert.False(Say(guard, _c).Tripped);

        // Without the reset, these thirteen messages would trip on the seventh of the _a/_b run.
        Alternate(guard, 6, _a, _b);
        Assert.False(Say(guard, _c).Tripped);
    }

    [Fact]
    public void Same_agent_twice_in_a_row_breaks_the_alternation()
    {
        var guard = Guard();
        Alternate(guard, 6, _a, _b);

        Assert.False(Say(guard, _b).Tripped);

        // The repeated _b starts a run of one; _a, _b, _a, _b, _a brings it to six, which is the limit and does not trip.
        Alternate(guard, 5, _a, _b);
    }

    [Fact]
    public void Ping_pong_runs_are_per_thread()
    {
        var guard = Guard();
        Alternate(guard, 6, _a, _b);

        // The same pair in another GC starts its own run.
        Assert.False(Say(guard, _a, gc: _otherGc).Tripped);
        Assert.False(Say(guard, _b, gc: _otherGc).Tripped);
    }

    [Fact]
    public void Thread_agent_messages_trip_after_the_limit_since_the_last_human_message()
    {
        var guard = Guard();
        var rotation = new[] { _a, _b, _c };

        for (var i = 0; i < 20; i++)
        {
            Assert.False(Say(guard, rotation[i % 3]).Tripped);
        }

        var verdict = Say(guard, _a);

        Assert.Equal(GuardKind.ThreadAgentMessages, verdict.Kind);
        Assert.Equal([_a, _b, _c], verdict.AgentIds);
    }

    [Fact]
    public void Human_message_resets_the_thread_agent_message_count()
    {
        var guard = Guard();
        var rotation = new[] { _a, _b, _c };
        for (var i = 0; i < 20; i++)
        {
            Say(guard, rotation[i % 3]);
        }

        Assert.False(guard.Check(new HumanMessage(_gc)).Tripped);

        for (var i = 0; i < 20; i++)
        {
            Assert.False(Say(guard, rotation[i % 3]).Tripped);
        }
    }

    [Fact]
    public void Tool_result_does_not_reset_the_thread_agent_message_count()
    {
        var guard = Guard();
        var rotation = new[] { _a, _b, _c };
        for (var i = 0; i < 20; i++)
        {
            Say(guard, rotation[i % 3]);
            guard.Check(new ToolResult(_gc));
        }

        Assert.Equal(GuardKind.ThreadAgentMessages, Say(guard, _a).Kind);
    }

    [Fact]
    public void Mention_depth_at_the_limit_does_not_trip()
    {
        Assert.False(Say(Guard(), _a, depth: 4).Tripped);
    }

    [Fact]
    public void Mention_depth_past_the_limit_trips_with_the_posting_agent()
    {
        var verdict = Say(Guard(), _b, depth: 5);

        Assert.Equal(GuardKind.MentionDepth, verdict.Kind);
        Assert.Equal([_b], verdict.AgentIds);
    }

    [Fact]
    public void Mention_depth_trip_uses_the_configured_limit()
    {
        var guard = Guard(new LoopGuardOptions { MentionDepth = 2 });

        Assert.False(Say(guard, _a, depth: 2).Tripped);
        Assert.Equal(GuardKind.MentionDepth, Say(guard, _a, depth: 3).Kind);
    }

    [Fact]
    public void Configured_ping_pong_limit_is_used()
    {
        var guard = Guard(new LoopGuardOptions { PingPongLimit = 2 });
        Alternate(guard, 2, _a, _b);

        Assert.Equal(GuardKind.PingPong, Say(guard, _a).Kind);
    }

    [Fact]
    public void Negative_mention_depth_is_refused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Say(Guard(), _a, depth: -1));
    }
}
