using Bazaar.Server.Security.KillSwitch;
using Microsoft.Extensions.Time.Testing;

namespace Bazaar.Server.Tests.Security.KillSwitch;

/// <summary>
/// Chaos tests (agent-misbehaviour campaign, doc 46 section 17.1 fault harness): an engage that races a resume must leave the
/// agent stopped once the engage has returned, in the cache and in the store.
/// </summary>
public class KillSwitchChaosTests
{
    private static readonly DateTimeOffset _start = new(2026, 11, 19, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Engage_that_lands_while_a_resume_is_releasing_keeps_the_agent_stopped()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = new InMemoryKillSwitchStateStore();
        var containment = new GatedContainment();
        var engine = new KillSwitchEngine(new InMemoryKillSwitchAuditSink(), containment, store, new FakeTimeProvider(_start));
        await engine.WaitUntilStateLoadedAsync(ct);

        await engine.EngageAgentAsync("owner-1", "agent-a", "First breach", ct);

        // The owner's resume clears the store, then blocks inside the host's release hook.
        var resume = engine.ResumeAgentAsync("agent-a", "Back to work", ReleaseRequests.Unsigned("agent-a", "owner-1"), ct);
        await containment.ReleaseEntered.Task.WaitAsync(ct);

        // A new breach lands while the release is in progress, and the engage returns.
        await engine.EngageAgentAsync(KillSwitchEngine.BreachActorId, "agent-a", "Second breach", ct);
        Assert.False(engine.Evaluate("agent-a", ToolEffect.Write).Allowed);

        containment.AllowRelease.SetResult();
        await resume;

        // The second stop was engaged after the release began, so it must still hold.
        Assert.False(engine.Evaluate("agent-a", ToolEffect.Write).Allowed);
        Assert.False(engine.Evaluate("agent-a", ToolEffect.Egress).Allowed);
        var stored = await store.LoadAsync(ct);
        Assert.Contains(stored.AgentStops, stop => stop.AgentId == "agent-a" && stop.Reason == "Second breach");

        // And a restarted host (new engine over the same store) agrees with the cache.
        var restarted = new KillSwitchEngine(new InMemoryKillSwitchAuditSink(), new RecordingContainment(), store, new FakeTimeProvider(_start));
        await restarted.WaitUntilStateLoadedAsync(ct);
        Assert.False(restarted.Evaluate("agent-a", ToolEffect.Write).Allowed);
    }

    [Fact]
    public async Task Engage_that_lands_before_the_resume_clears_the_store_is_lifted_by_that_resume_consistently()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = new InMemoryKillSwitchStateStore();
        var engine = new KillSwitchEngine(new InMemoryKillSwitchAuditSink(), new RecordingContainment(), store, new FakeTimeProvider(_start));
        await engine.WaitUntilStateLoadedAsync(ct);

        // Interleave many engage and resume calls for one agent. Whatever order they land in, the cache and the store must agree
        // at the end: a stop that the cache holds is in the store, and the other way round.
        for (var round = 0; round < 3000; round++)
        {
            var engage = Task.Run(() => engine.EngageAgentAsync(KillSwitchEngine.BreachActorId, "agent-a", "Breach " + round, ct), ct);
            var resume = Task.Run(() => engine.ResumeAgentAsync("agent-a", "Resume", ReleaseRequests.Unsigned("agent-a", "owner-1"), ct), ct);
            await Task.WhenAll(engage, resume);

            var cached = engine.GetStatus().AgentStops.Any(stop => stop.AgentId == "agent-a");
            var stored = (await store.LoadAsync(ct)).AgentStops.Any(stop => stop.AgentId == "agent-a");
            Assert.Equal(stored, cached);
        }
    }

    [Fact]
    public async Task No_write_is_allowed_after_engage_returns_while_evaluators_hammer_the_engine()
    {
        var ct = TestContext.Current.CancellationToken;
        var engine = new KillSwitchEngine(new InMemoryKillSwitchAuditSink(), new RecordingContainment(), new FakeTimeProvider(_start));
        await engine.WaitUntilStateLoadedAsync(ct);

        var engaged = 0;
        var leaks = 0;
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var evaluators = Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                // Read the flag first: if the engage had returned before this check, the decision must be a deny.
                var engagedBefore = Volatile.Read(ref engaged) == 1;
                if (engagedBefore && engine.Evaluate("agent-a", ToolEffect.Write).Allowed)
                {
                    Interlocked.Increment(ref leaks);
                }
            }
        }, ct)).ToArray();

        await Task.Delay(20, ct);
        await engine.EngageGlobalAsync("owner-1", "Freeze", ct);
        Volatile.Write(ref engaged, 1);
        await Task.Delay(50, ct);
        await stop.CancelAsync();
        await Task.WhenAll(evaluators);

        Assert.Equal(0, leaks);
    }

    /// <summary>A containment whose release blocks until the test lets it finish.</summary>
    private sealed class GatedContainment : IAgentContainment
    {
        public TaskCompletionSource ReleaseEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource AllowRelease { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task ContainAsync(string agentId, string actorId, CancellationToken ct = default) => Task.CompletedTask;

        public Task<ReleaseChallenge?> IssueReleaseChallengeAsync(string agentId, string deviceId, CancellationToken ct = default) =>
            Task.FromResult<ReleaseChallenge?>(null);

        public async Task<ContainmentReleaseResult> ReleaseAsync(string agentId, ContainmentReleaseRequest request, CancellationToken ct = default)
        {
            ReleaseEntered.TrySetResult();
            await AllowRelease.Task.WaitAsync(ct);
            return new ContainmentReleaseResult(ContainmentReleaseOutcome.Released);
        }
    }
}
