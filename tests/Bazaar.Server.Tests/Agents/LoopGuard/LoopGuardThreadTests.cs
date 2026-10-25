using Bazaar.Server.Agents.LoopGuard;
using Bazaar.Testing;

namespace Bazaar.Server.Tests.Agents.LoopGuard;

public class LoopGuardThreadTests
{
    private static readonly Guid _gc = Guid.Parse("0196a6f0-0000-7000-8000-000000000201");
    private static readonly Guid _otherGc = Guid.Parse("0196a6f0-0000-7000-8000-000000000202");
    private const string A = "agent-a";
    private const string B = "agent-b";
    private const string C = "agent-c";

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
        Alternate(guard, 6, A, B);

        var verdict = Say(guard, A);

        Assert.True(verdict.Tripped);
        Assert.Equal(GuardKind.PingPong, verdict.Kind);
        Assert.Equal([A, B], verdict.AgentIds);
    }

    [Fact]
    public void Ping_pong_at_the_limit_does_not_trip()
    {
        var guard = Guard();

        Alternate(guard, 6, A, B);
    }

    [Fact]
    public void Human_message_resets_ping_pong()
    {
        var guard = Guard();
        Alternate(guard, 6, A, B);

        Assert.False(guard.Check(new HumanMessage(_gc)).Tripped);

        Alternate(guard, 6, A, B);
    }

    [Fact]
    public void Tool_result_resets_ping_pong()
    {
        var guard = Guard();
        Alternate(guard, 6, A, B);

        Assert.False(guard.Check(new ToolResult(_gc)).Tripped);

        Alternate(guard, 6, A, B);
    }

    [Fact]
    public void Third_agent_message_resets_ping_pong()
    {
        var guard = Guard();
        Alternate(guard, 6, A, B);

        Assert.False(Say(guard, C).Tripped);

        // Without the reset, these thirteen messages would trip on the seventh of the A/B run.
        Alternate(guard, 6, A, B);
        Assert.False(Say(guard, C).Tripped);
    }

    [Fact]
    public void Same_agent_twice_in_a_row_breaks_the_alternation()
    {
        var guard = Guard();
        Alternate(guard, 6, A, B);

        Assert.False(Say(guard, B).Tripped);

        // The repeated B starts a run of one; A, B, A, B, A brings it to six, which is the limit and does not trip.
        Alternate(guard, 5, A, B);
    }

    [Fact]
    public void Ping_pong_runs_are_per_thread()
    {
        var guard = Guard();
        Alternate(guard, 6, A, B);

        // The same pair in another GC starts its own run.
        Assert.False(Say(guard, A, gc: _otherGc).Tripped);
        Assert.False(Say(guard, B, gc: _otherGc).Tripped);
    }

    [Fact]
    public void Thread_agent_messages_trip_after_the_limit_since_the_last_human_message()
    {
        var guard = Guard();
        var rotation = new[] { A, B, C };

        for (var i = 0; i < 20; i++)
        {
            Assert.False(Say(guard, rotation[i % 3]).Tripped);
        }

        var verdict = Say(guard, A);

        Assert.Equal(GuardKind.ThreadAgentMessages, verdict.Kind);
        Assert.Equal([A, B, C], verdict.AgentIds);
    }

    [Fact]
    public void Human_message_resets_the_thread_agent_message_count()
    {
        var guard = Guard();
        var rotation = new[] { A, B, C };
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
        var rotation = new[] { A, B, C };
        for (var i = 0; i < 20; i++)
        {
            Say(guard, rotation[i % 3]);
            guard.Check(new ToolResult(_gc));
        }

        Assert.Equal(GuardKind.ThreadAgentMessages, Say(guard, A).Kind);
    }

    [Fact]
    public void Mention_depth_at_the_limit_does_not_trip()
    {
        Assert.False(Say(Guard(), A, depth: 4).Tripped);
    }

    [Fact]
    public void Mention_depth_past_the_limit_trips_with_the_posting_agent()
    {
        var verdict = Say(Guard(), B, depth: 5);

        Assert.Equal(GuardKind.MentionDepth, verdict.Kind);
        Assert.Equal([B], verdict.AgentIds);
    }

    [Fact]
    public void Mention_depth_trip_uses_the_configured_limit()
    {
        var guard = Guard(new LoopGuardOptions { MentionDepth = 2 });

        Assert.False(Say(guard, A, depth: 2).Tripped);
        Assert.Equal(GuardKind.MentionDepth, Say(guard, A, depth: 3).Kind);
    }

    [Fact]
    public void Configured_ping_pong_limit_is_used()
    {
        var guard = Guard(new LoopGuardOptions { PingPongLimit = 2 });
        Alternate(guard, 2, A, B);

        Assert.Equal(GuardKind.PingPong, Say(guard, A).Kind);
    }

    [Fact]
    public void Negative_mention_depth_is_refused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Say(Guard(), A, depth: -1));
    }
}
