using Bazaar.Server.Security.KillSwitch;
using Microsoft.Extensions.Time.Testing;

namespace Bazaar.Server.Tests.Security.KillSwitch;

public class KillSwitchEngineTests
{
    private static readonly DateTimeOffset _start = new(2026, 11, 15, 9, 0, 0, TimeSpan.Zero);

    private readonly FakeTimeProvider _time = new(_start);
    private readonly InMemoryKillSwitchAuditSink _audit = new();
    private readonly RecordingContainment _containment = new();
    private readonly KillSwitchEngine _engine;

    public KillSwitchEngineTests()
    {
        _engine = new KillSwitchEngine(_audit, _containment, _time);
    }

    [Fact]
    public async Task Global_stop_denies_write_and_egress_but_allows_read()
    {
        await _engine.EngageGlobalAsync("owner-1", "Suspicious traffic", TestContext.Current.CancellationToken);

        Assert.False(_engine.Evaluate("agent-a", ToolEffect.Write).Allowed);
        Assert.False(_engine.Evaluate("agent-a", ToolEffect.Egress).Allowed);
        Assert.True(_engine.Evaluate("agent-a", ToolEffect.Read).Allowed);
    }

    [Fact]
    public async Task Global_stop_denial_names_the_global_scope_and_reason()
    {
        await _engine.EngageGlobalAsync("owner-1", "Suspicious traffic", TestContext.Current.CancellationToken);

        var decision = _engine.Evaluate("agent-b", ToolEffect.Egress);

        Assert.False(decision.Allowed);
        Assert.Equal(KillSwitchScope.Global, decision.Scope);
        Assert.Equal("Suspicious traffic", decision.Reason);
    }

    [Fact]
    public async Task Global_stop_applies_to_every_agent()
    {
        await _engine.EngageGlobalAsync("owner-1", "Freeze", TestContext.Current.CancellationToken);

        Assert.False(_engine.Evaluate("agent-a", ToolEffect.Write).Allowed);
        Assert.False(_engine.Evaluate("agent-z", ToolEffect.Write).Allowed);
    }

    [Fact]
    public async Task Agent_stop_denies_every_effect_for_that_agent_only()
    {
        await _engine.EngageAgentAsync("owner-1", "agent-a", "Rate breach", TestContext.Current.CancellationToken);

        Assert.False(_engine.Evaluate("agent-a", ToolEffect.Read).Allowed);
        Assert.False(_engine.Evaluate("agent-a", ToolEffect.Write).Allowed);
        Assert.False(_engine.Evaluate("agent-a", ToolEffect.Egress).Allowed);

        Assert.True(_engine.Evaluate("agent-b", ToolEffect.Read).Allowed);
        Assert.True(_engine.Evaluate("agent-b", ToolEffect.Write).Allowed);
        Assert.True(_engine.Evaluate("agent-b", ToolEffect.Egress).Allowed);
    }

    [Fact]
    public async Task Agent_stop_denial_names_the_agent_scope()
    {
        await _engine.EngageAgentAsync("owner-1", "agent-a", "Rate breach", TestContext.Current.CancellationToken);

        var decision = _engine.Evaluate("agent-a", ToolEffect.Read);

        Assert.Equal(KillSwitchScope.Agent, decision.Scope);
        Assert.Equal("Rate breach", decision.Reason);
    }

    [Fact]
    public async Task Blank_actor_is_rejected_for_every_engage_and_resume()
    {
        foreach (var actor in new[] { "", "   " })
        {
            await Assert.ThrowsAsync<ArgumentException>(() => _engine.EngageGlobalAsync(actor, "reason", TestContext.Current.CancellationToken));
            await Assert.ThrowsAsync<ArgumentException>(() => _engine.ResumeGlobalAsync(actor, "reason", TestContext.Current.CancellationToken));
            await Assert.ThrowsAsync<ArgumentException>(() => _engine.EngageAgentAsync(actor, "agent-a", "reason", TestContext.Current.CancellationToken));
            await Assert.ThrowsAsync<ArgumentException>(() => _engine.ResumeAgentAsync(actor, "agent-a", "reason", TestContext.Current.CancellationToken));
        }

        Assert.Empty(_audit.Entries);
        Assert.True(_engine.Evaluate("agent-a", ToolEffect.Write).Allowed);
    }

    [Fact]
    public async Task Blank_reason_is_rejected()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => _engine.EngageGlobalAsync("owner-1", " ", TestContext.Current.CancellationToken));
        Assert.Empty(_audit.Entries);
    }

    [Fact]
    public void Blank_agent_id_is_rejected_by_evaluate()
    {
        Assert.Throws<ArgumentException>(() => _engine.Evaluate("", ToolEffect.Read));
    }

    [Fact]
    public async Task Every_engage_and_resume_produces_one_audit_record()
    {
        await _engine.EngageGlobalAsync("owner-1", "Freeze", TestContext.Current.CancellationToken);
        await _engine.ResumeGlobalAsync("owner-1", "Cleared", TestContext.Current.CancellationToken);
        await _engine.EngageAgentAsync("owner-1", "agent-a", "Stop", TestContext.Current.CancellationToken);
        await _engine.ResumeAgentAsync("owner-1", "agent-a", "Back", TestContext.Current.CancellationToken);

        var expected = new (KillSwitchAction Action, KillSwitchScope Scope, string? AgentId)[]
        {
            (KillSwitchAction.Engaged, KillSwitchScope.Global, null),
            (KillSwitchAction.Resumed, KillSwitchScope.Global, null),
            (KillSwitchAction.Engaged, KillSwitchScope.Agent, "agent-a"),
            (KillSwitchAction.Resumed, KillSwitchScope.Agent, "agent-a"),
        };
        Assert.Equal(expected, _audit.Entries.Select(e => (e.Action, e.Scope, e.AgentId)));
    }

    [Fact]
    public async Task Audit_records_carry_actor_reason_and_engine_time()
    {
        await _engine.EngageAgentAsync("owner-1", "agent-a", "Rate breach", TestContext.Current.CancellationToken);

        var entry = Assert.Single(_audit.Entries);
        Assert.Equal("owner-1", entry.ActorId);
        Assert.Equal("Rate breach", entry.Reason);
        Assert.Equal(_start, entry.Ts);
    }

    [Fact]
    public async Task Resume_restores_allowed_state_for_global_and_agent_stops()
    {
        await _engine.EngageGlobalAsync("owner-1", "Freeze", TestContext.Current.CancellationToken);
        await _engine.EngageAgentAsync("owner-1", "agent-a", "Stop", TestContext.Current.CancellationToken);

        await _engine.ResumeGlobalAsync("owner-1", "Cleared", TestContext.Current.CancellationToken);
        Assert.True(_engine.Evaluate("agent-b", ToolEffect.Write).Allowed);
        Assert.False(_engine.Evaluate("agent-a", ToolEffect.Write).Allowed);

        await _engine.ResumeAgentAsync("owner-1", "agent-a", "Cleared", TestContext.Current.CancellationToken);
        Assert.True(_engine.Evaluate("agent-a", ToolEffect.Write).Allowed);
        Assert.True(_engine.Evaluate("agent-a", ToolEffect.Egress).Allowed);
        Assert.Empty(_engine.GetStatus().AgentStops);
        Assert.Null(_engine.GetStatus().Global);
    }

    [Fact]
    public async Task Resume_of_global_stop_does_not_clear_agent_stops()
    {
        await _engine.EngageGlobalAsync("owner-1", "Freeze", TestContext.Current.CancellationToken);
        await _engine.EngageAgentAsync("owner-1", "agent-a", "Stop", TestContext.Current.CancellationToken);

        await _engine.ResumeGlobalAsync("owner-1", "Cleared", TestContext.Current.CancellationToken);

        Assert.False(_engine.Evaluate("agent-a", ToolEffect.Read).Allowed);
    }

    [Fact]
    public async Task Agent_engage_calls_containment_and_resume_releases_it()
    {
        await _engine.EngageAgentAsync("owner-1", "agent-a", "Stop", TestContext.Current.CancellationToken);
        await _engine.ResumeAgentAsync("owner-1", "agent-a", "Back", TestContext.Current.CancellationToken);

        Assert.Equal(["contain:agent-a", "release:agent-a"], _containment.Calls);
    }

    [Fact]
    public async Task Global_engage_does_not_call_per_agent_containment()
    {
        await _engine.EngageGlobalAsync("owner-1", "Freeze", TestContext.Current.CancellationToken);
        await _engine.ResumeGlobalAsync("owner-1", "Cleared", TestContext.Current.CancellationToken);

        Assert.Empty(_containment.Calls);
    }

    [Fact]
    public async Task Failing_containment_leaves_the_agent_denied()
    {
        _containment.FailContain = true;

        await Assert.ThrowsAsync<InvalidOperationException>(() => _engine.EngageAgentAsync("owner-1", "agent-a", "Stop", TestContext.Current.CancellationToken));

        Assert.False(_engine.Evaluate("agent-a", ToolEffect.Write).Allowed);
        Assert.Single(_audit.Entries);
    }

    [Fact]
    public async Task Failing_release_leaves_the_agent_denied()
    {
        await _engine.EngageAgentAsync("owner-1", "agent-a", "Stop", TestContext.Current.CancellationToken);
        _containment.FailRelease = true;

        await Assert.ThrowsAsync<InvalidOperationException>(() => _engine.ResumeAgentAsync("owner-1", "agent-a", "Back", TestContext.Current.CancellationToken));

        Assert.False(_engine.Evaluate("agent-a", ToolEffect.Write).Allowed);
    }

    [Fact]
    public async Task Breach_hook_engages_an_agent_stop_with_the_breach_actor()
    {
        await _engine.ReportBreachAsync("agent-a", "Token budget exceeded", TestContext.Current.CancellationToken);

        var decision = _engine.Evaluate("agent-a", ToolEffect.Egress);
        Assert.False(decision.Allowed);
        Assert.Equal(KillSwitchScope.Agent, decision.Scope);

        var entry = Assert.Single(_audit.Entries);
        Assert.Equal(KillSwitchEngine.BreachActorId, entry.ActorId);
        Assert.Equal(KillSwitchAction.Engaged, entry.Action);
        Assert.Equal(["contain:agent-a"], _containment.Calls);
    }

    [Fact]
    public async Task Breach_hook_never_engages_the_global_stop()
    {
        await _engine.ReportBreachAsync("agent-a", "Token budget exceeded", TestContext.Current.CancellationToken);

        Assert.Null(_engine.GetStatus().Global);
        Assert.True(_engine.Evaluate("agent-b", ToolEffect.Write).Allowed);
    }

    [Fact]
    public async Task Failing_audit_sink_keeps_the_stop_in_place_for_engage_and_resume()
    {
        var engine = new KillSwitchEngine(new FailingKillSwitchAuditSink(), _containment, _time);

        // The stop is set before the record is written, so a failed record still denies.
        await Assert.ThrowsAsync<InvalidOperationException>(() => engine.EngageGlobalAsync("owner-1", "Freeze", TestContext.Current.CancellationToken));
        Assert.False(engine.Evaluate("agent-a", ToolEffect.Write).Allowed);

        // A resume whose record cannot be written does not lift the stop.
        await Assert.ThrowsAsync<InvalidOperationException>(() => engine.ResumeGlobalAsync("owner-1", "Cleared", TestContext.Current.CancellationToken));
        Assert.False(engine.Evaluate("agent-a", ToolEffect.Write).Allowed);
    }

    [Fact]
    public async Task Status_lists_global_and_agent_stops_with_engine_time()
    {
        await _engine.EngageGlobalAsync("owner-1", "Freeze", TestContext.Current.CancellationToken);
        _time.Advance(TimeSpan.FromMinutes(5));
        await _engine.EngageAgentAsync("owner-1", "agent-b", "Stop B", TestContext.Current.CancellationToken);
        await _engine.EngageAgentAsync("owner-1", "agent-a", "Stop A", TestContext.Current.CancellationToken);

        var status = _engine.GetStatus();

        Assert.NotNull(status.Global);
        Assert.Equal(_start, status.Global.Since);
        Assert.Equal(["agent-a", "agent-b"], status.AgentStops.Select(s => s.AgentId));
        Assert.Equal(_start.AddMinutes(5), status.AgentStops[0].Since);
    }

    [Fact]
    public async Task Concurrent_engage_resume_and_evaluate_do_not_throw()
    {
        var tasks = new List<Task>();
        for (var i = 0; i < 8; i++)
        {
            var agent = "agent-" + i;
            tasks.Add(Task.Run(async () =>
            {
                for (var n = 0; n < 50; n++)
                {
                    await _engine.EngageAgentAsync("owner-1", agent, "Stop", TestContext.Current.CancellationToken);
                    await _engine.ResumeAgentAsync("owner-1", agent, "Back", TestContext.Current.CancellationToken);
                }
            }, TestContext.Current.CancellationToken));
            tasks.Add(Task.Run(async () =>
            {
                for (var n = 0; n < 50; n++)
                {
                    await _engine.EngageGlobalAsync("owner-1", "Freeze", TestContext.Current.CancellationToken);
                    await _engine.ResumeGlobalAsync("owner-1", "Cleared", TestContext.Current.CancellationToken);
                }
            }, TestContext.Current.CancellationToken));
            tasks.Add(Task.Run(() =>
            {
                for (var n = 0; n < 500; n++)
                {
                    _engine.Evaluate(agent, ToolEffect.Write);
                    _engine.Evaluate(agent, ToolEffect.Read);
                    _engine.GetStatus();
                }
            }, TestContext.Current.CancellationToken));
        }

        await Task.WhenAll(tasks);

        Assert.Equal(8 * 50 * 4, _audit.Entries.Count);
        Assert.Empty(_engine.GetStatus().AgentStops);
        Assert.Null(_engine.GetStatus().Global);
    }
}
