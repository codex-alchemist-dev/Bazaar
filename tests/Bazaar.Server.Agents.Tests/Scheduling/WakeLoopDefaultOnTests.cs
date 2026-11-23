using Bazaar.Server.Agents;
using Bazaar.Server.Agents.Data;
using Bazaar.Server.Agents.LoopGuard;
using Bazaar.Server.Agents.Scheduling;
using Bazaar.Server.Agents.Turns;
using Bazaar.Server.Agents.Turns.Start;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Bazaar.Server.Tests.Agents.Scheduling;

/// <summary>
/// The default-on wake loop with fake agents (overnight plan item 41): a fake turn runner stands in for the model, a fake inbox
/// reader for the Postgres inbox, and the real <see cref="LoopGuardFacade"/> for the guard. No network, no database.
/// </summary>
public sealed class WakeLoopDefaultOnTests
{
    private readonly FakeTurnRunner _runner = new();
    private readonly FakeTimeProvider _time = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private InboxAgentScheduler Scheduler(IDueAgentReader due, ILoopGuard guard, IAgentWakeGate? gate = null) => new(
        due,
        new TurnStarter(new FakeDueInboxReader(), new NoWakeChainDepthSource(), _runner, new TurnStarterOptions()),
        guard,
        new FixedGrants([]),
        new AgentSchedulerOptions(),
        _time,
        NullLogger<InboxAgentScheduler>.Instance,
        gate);

    [Fact]
    public void A_fresh_configuration_has_the_wake_loop_on()
    {
        Assert.True(new AgentSchedulerOptions().Enabled);
    }

    [Fact]
    public async Task A_mention_wakes_the_agent_exactly_once()
    {
        var agent = Guid.CreateVersion7();
        var inbox = new PendingInbox(agent);
        _runner.OnRun = (request, _) =>
        {
            inbox.Consume(request.AgentId);
            return Task.FromResult(TurnEndReason.FinalMessage);
        };
        var scheduler = Scheduler(inbox, new FakeLoopGuard());

        var first = await scheduler.RunOnceAsync(Ct);
        var second = await scheduler.RunOnceAsync(Ct);
        var third = await scheduler.RunOnceAsync(Ct);

        Assert.Equal(1, first.TurnsStarted);
        Assert.Equal(AgentSchedulerPass.Empty, second);
        Assert.Equal(AgentSchedulerPass.Empty, third);
        Assert.Equal(agent, Assert.Single(_runner.Requests).AgentId);
    }

    [Fact]
    public async Task A_loop_between_two_agents_is_stopped_by_the_loop_guard()
    {
        var a = Guid.CreateVersion7();
        var b = Guid.CreateVersion7();
        // Each turn mentions the other agent, so both stay due on every pass: a ping-pong loop.
        var inbox = new PendingInbox(a, b);
        var guard = new LoopGuardFacade(_time, new LoopGuardOptions { WakesPerHour = 4, ScoredWakesPerHour = 4 });
        var scheduler = Scheduler(inbox, guard);

        var skipped = 0;
        for (var i = 0; i < 20; i++)
        {
            skipped += (await scheduler.RunOnceAsync(Ct)).SkippedByGuard;
            _time.Advance(TimeSpan.FromSeconds(2));
        }

        Assert.True(skipped > 0, "The guard never tripped.");
        foreach (var agent in new[] { a, b })
            Assert.InRange(_runner.Requests.Count(r => r.AgentId == agent), 1, 4);
    }

    [Fact]
    public async Task The_kill_switch_stops_wakes_and_leaves_the_inbox_pending()
    {
        var agent = Guid.CreateVersion7();
        var gate = new SwitchGate();
        var guard = new FakeLoopGuard();
        var scheduler = Scheduler(new PendingInbox(agent), guard, gate);

        gate.Engaged = true;
        var stopped = await scheduler.RunOnceAsync(Ct);

        Assert.Equal(new AgentSchedulerPass(1, 0, 0, 0, 0, 0, 1), stopped);
        Assert.Empty(_runner.Requests);
        Assert.Empty(guard.Wakes);

        gate.Engaged = false;
        var resumed = await scheduler.RunOnceAsync(Ct);

        Assert.Equal(1, resumed.TurnsStarted);
        Assert.Single(_runner.Requests);
    }

    private sealed class SwitchGate : IAgentWakeGate
    {
        public volatile bool Engaged;

        public WakeGateDecision Check(Guid agentId) => Engaged ? WakeGateDecision.Deny("stopped") : WakeGateDecision.Allow;
    }

    /// <summary>Agents that stay due until <see cref="Consume"/> is called for them. A set that is never consumed is a loop.</summary>
    private sealed class PendingInbox(params Guid[] agents) : IDueAgentReader
    {
        private readonly Lock _gate = new();
        private readonly HashSet<Guid> _due = [.. agents];

        public void Consume(Guid agent)
        {
            lock (_gate)
                _due.Remove(agent);
        }

        public Task<IReadOnlyList<DueAgent>> PeekDueAgentsAsync(int limit, CancellationToken ct)
        {
            lock (_gate)
            {
                IReadOnlyList<DueAgent> due = [.. _due.Take(limit).Select(id => new DueAgent(id, 1, DateTimeOffset.UnixEpoch))];
                return Task.FromResult(due);
            }
        }
    }
}
