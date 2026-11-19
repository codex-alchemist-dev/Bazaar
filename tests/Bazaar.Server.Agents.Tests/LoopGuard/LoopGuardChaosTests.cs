using Bazaar.Server.Agents.LoopGuard;
using Microsoft.Extensions.Time.Testing;

namespace Bazaar.Server.Agents.Tests.LoopGuard;

/// <summary>
/// Chaos tests (agent-misbehaviour campaign): runaway agents ping-pong, rotate in threes, interleave tool results to reset the
/// ping-pong run, or repeat one tool call. The number of agent posts the guard lets through without a human must stay bounded.
/// </summary>
public sealed class LoopGuardChaosTests
{
    private static readonly DateTimeOffset _start = new(2026, 11, 19, 9, 0, 0, TimeSpan.Zero);

    private static readonly Guid _gc = Guid.Parse("00000000-0000-7000-8000-000000000003");

    private readonly LoopGuardOptions _options = new();

    /// <summary>Feeds every event to the guard and counts the agent posts it let through.</summary>
    private static int AllowedPosts(LoopGuardFacade guard, IEnumerable<LoopEvent> events) =>
        events.Select(loopEvent => (loopEvent, verdict: guard.Check(loopEvent)))
            .Count(pair => pair.loopEvent is AgentMessage && !pair.verdict.Tripped);

    [Fact]
    public void Two_agents_mentioning_each_other_forever_are_cut_off_at_the_ping_pong_limit()
    {
        var guard = new LoopGuardFacade(new FakeTimeProvider(_start), _options);
        var events = Enumerable.Range(0, 10_000).Select(i => new AgentMessage(_gc, i % 2 == 0 ? "charles" : "dana", 0));

        var allowed = AllowedPosts(guard, events);

        Assert.Equal(_options.PingPongLimit, allowed);
    }

    [Fact]
    public void An_agent_mentioning_itself_forever_is_cut_off_at_the_thread_limit()
    {
        var guard = new LoopGuardFacade(new FakeTimeProvider(_start), _options);
        var events = Enumerable.Range(0, 10_000).Select(_ => new AgentMessage(_gc, "charles", 0));

        Assert.Equal(_options.ThreadAgentMessages, AllowedPosts(guard, events));
    }

    [Fact]
    public void Three_agents_rotating_are_cut_off_at_the_thread_limit()
    {
        var guard = new LoopGuardFacade(new FakeTimeProvider(_start), _options);
        string[] agents = ["charles", "dana", "eve"];
        var events = Enumerable.Range(0, 10_000).Select(i => new AgentMessage(_gc, agents[i % 3], 0));

        Assert.Equal(_options.ThreadAgentMessages, AllowedPosts(guard, events));
    }

    [Fact]
    public void Interleaving_tool_results_resets_ping_pong_but_not_the_thread_limit()
    {
        var guard = new LoopGuardFacade(new FakeTimeProvider(_start), _options);
        var events = Enumerable.Range(0, 10_000).SelectMany(i => new LoopEvent[]
        {
            new AgentMessage(_gc, i % 2 == 0 ? "charles" : "dana", 0),
            new ToolResult(_gc),
        });

        Assert.Equal(_options.ThreadAgentMessages, AllowedPosts(guard, events));
    }

    [Fact]
    public void A_tripped_thread_stays_tripped_until_a_human_speaks()
    {
        var guard = new LoopGuardFacade(new FakeTimeProvider(_start), _options);
        for (var i = 0; i < 100; i++)
        {
            guard.Check(new AgentMessage(_gc, i % 2 == 0 ? "charles" : "dana", 0));
        }

        Assert.True(guard.Check(new AgentMessage(_gc, "charles", 0)).Tripped);
        Assert.False(guard.Check(new HumanMessage(_gc)).Tripped);
        Assert.False(guard.Check(new AgentMessage(_gc, "charles", 0)).Tripped);
    }

    [Fact]
    public void The_same_tool_call_repeated_is_cut_off_and_a_clock_step_back_does_not_reset_wakes()
    {
        var time = new SteppingClock { Now = _start.AddHours(3) };
        var guard = new LoopGuardFacade(time, _options);

        var repeats = Enumerable.Range(0, 1000).Count(_ => !guard.Check(new ToolCall("charles", "shell.run|rm -rf /")).Tripped);
        Assert.Equal(_options.RepeatToolCall, repeats);

        var wakes = Enumerable.Range(0, 1000).Count(_ => !guard.Check(new AgentWake("charles", WakeKind.Standard)).Tripped);
        Assert.Equal(_options.WakesPerHour, wakes);

        // An NTP step back by 30 minutes: the recorded wakes look like they are in the future and still count.
        time.Now = _start.AddHours(2.5);
        Assert.True(guard.Check(new AgentWake("charles", WakeKind.Standard)).Tripped);
    }

    [Fact]
    public void Concurrent_posts_from_many_threads_never_exceed_the_thread_limit()
    {
        var guard = new LoopGuardFacade(new FakeTimeProvider(_start), _options);
        var allowed = 0;

        Parallel.For(0, 20_000, i =>
        {
            if (!guard.Check(new AgentMessage(_gc, "agent-" + (i % 7), 0)).Tripped)
            {
                Interlocked.Increment(ref allowed);
            }
        });

        Assert.True(allowed <= _options.ThreadAgentMessages, $"{allowed} posts were allowed.");
    }

    /// <summary>A clock that may step backwards, which <see cref="FakeTimeProvider"/> refuses to do.</summary>
    private sealed class SteppingClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; }

        public override DateTimeOffset GetUtcNow() => Now;
    }
}
