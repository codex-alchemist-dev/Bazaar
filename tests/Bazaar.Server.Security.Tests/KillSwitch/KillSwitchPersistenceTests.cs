using System.Security.Cryptography;
using Bazaar.Approvals;
using Bazaar.Server.Modules.Health;
using Bazaar.Server.Security.Approvals;
using Bazaar.Server.Security.KillSwitch;
using Microsoft.Extensions.Time.Testing;

namespace Bazaar.Server.Security.Tests.KillSwitch;

/// <summary>
/// Kill switch and containment state that survives a restart: a new engine or adapter over the same store recovers every stop
/// and contained agent, a failed store write leaves the state unchanged, and the engine fails closed until its state is loaded.
/// </summary>
public class KillSwitchPersistenceTests
{
    private static readonly DateTimeOffset _start = new(2026, 11, 15, 9, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly FakeTimeProvider _time = new(_start);

    [Fact]
    public async Task A_new_engine_over_the_same_store_recovers_a_global_stop_and_an_agent_stop()
    {
        var store = new InMemoryKillSwitchStateStore();
        var first = new KillSwitchEngine(new InMemoryKillSwitchAuditSink(), new RecordingContainment(), store, _time);
        await first.EngageGlobalAsync("owner-1", "Suspicious traffic", Ct);
        await first.EngageAgentAsync("owner-1", "agent-a", "Rate breach", Ct);

        var restarted = new KillSwitchEngine(new InMemoryKillSwitchAuditSink(), new RecordingContainment(), store, _time);

        var global = restarted.Evaluate("agent-b", ToolEffect.Write);
        Assert.False(global.Allowed);
        Assert.Equal(KillSwitchScope.Global, global.Scope);
        Assert.Equal("Suspicious traffic", global.Reason);

        var agent = restarted.Evaluate("agent-a", ToolEffect.Read);
        Assert.False(agent.Allowed);
        Assert.Equal(KillSwitchScope.Agent, agent.Scope);
        Assert.Equal("Rate breach", agent.Reason);

        var status = restarted.GetStatus();
        Assert.Equal("owner-1", status.Global!.ActorId);
        Assert.Equal(_start, status.Global.Since);
        Assert.Equal(["agent-a"], status.AgentStops.Select(s => s.AgentId));
    }

    [Fact]
    public async Task A_resume_is_recovered_too()
    {
        var store = new InMemoryKillSwitchStateStore();
        var containment = new RecordingContainment();
        var first = new KillSwitchEngine(new InMemoryKillSwitchAuditSink(), containment, store, _time);
        await first.EngageAgentAsync("owner-1", "agent-a", "Rate breach", Ct);
        await first.ResumeAgentAsync("agent-a", "Cleared", EngineRequest("agent-a", "owner-1"), Ct);

        var restarted = new KillSwitchEngine(new InMemoryKillSwitchAuditSink(), containment, store, _time);

        Assert.True(restarted.Evaluate("agent-a", ToolEffect.Write).Allowed);
    }

    [Fact]
    public async Task A_failed_global_engage_write_leaves_the_stop_unchanged()
    {
        var store = new ControllableKillSwitchStateStore(new InMemoryKillSwitchStateStore()) { FailWrites = true };
        var audit = new InMemoryKillSwitchAuditSink();
        var engine = new KillSwitchEngine(audit, new RecordingContainment(), store, _time);

        await Assert.ThrowsAsync<InvalidOperationException>(() => engine.EngageGlobalAsync("owner-1", "Freeze", Ct));

        Assert.True(engine.Evaluate("agent-a", ToolEffect.Write).Allowed);
        Assert.Null(engine.GetStatus().Global);
        Assert.Empty(audit.Entries);
    }

    [Fact]
    public async Task A_failed_agent_engage_write_leaves_the_agent_running_and_does_not_contain_it()
    {
        var store = new ControllableKillSwitchStateStore(new InMemoryKillSwitchStateStore()) { FailWrites = true };
        var containment = new RecordingContainment();
        var engine = new KillSwitchEngine(new InMemoryKillSwitchAuditSink(), containment, store, _time);

        await Assert.ThrowsAsync<InvalidOperationException>(() => engine.EngageAgentAsync("owner-1", "agent-a", "Rate breach", Ct));

        Assert.True(engine.Evaluate("agent-a", ToolEffect.Write).Allowed);
        Assert.Empty(engine.GetStatus().AgentStops);
        Assert.Empty(containment.Calls);
    }

    [Fact]
    public async Task A_failed_global_resume_write_keeps_the_stop()
    {
        var store = new ControllableKillSwitchStateStore(new InMemoryKillSwitchStateStore());
        var engine = new KillSwitchEngine(new InMemoryKillSwitchAuditSink(), new RecordingContainment(), store, _time);
        await engine.EngageGlobalAsync("owner-1", "Freeze", Ct);
        store.FailWrites = true;

        await Assert.ThrowsAsync<InvalidOperationException>(() => engine.ResumeGlobalAsync("owner-1", "Cleared", Ct));

        Assert.False(engine.Evaluate("agent-a", ToolEffect.Write).Allowed);
        Assert.NotNull(engine.GetStatus().Global);
    }

    [Fact]
    public async Task A_failed_agent_resume_write_keeps_the_agent_denied()
    {
        var store = new ControllableKillSwitchStateStore(new InMemoryKillSwitchStateStore());
        var engine = new KillSwitchEngine(new InMemoryKillSwitchAuditSink(), new RecordingContainment(), store, _time);
        await engine.EngageAgentAsync("owner-1", "agent-a", "Rate breach", Ct);
        store.FailWrites = true;

        await Assert.ThrowsAsync<InvalidOperationException>(() => engine.ResumeAgentAsync("agent-a", "Cleared", EngineRequest("agent-a", "owner-1"), Ct));

        Assert.False(engine.Evaluate("agent-a", ToolEffect.Read).Allowed);
        Assert.Single(engine.GetStatus().AgentStops);
    }

    [Fact]
    public async Task A_denied_release_never_clears_the_stored_stop()
    {
        var inner = new InMemoryKillSwitchStateStore();
        var store = new ControllableKillSwitchStateStore(inner);
        var containment = new RecordingContainment();
        var engine = new KillSwitchEngine(new InMemoryKillSwitchAuditSink(), containment, store, _time);
        await engine.EngageAgentAsync("owner-1", "agent-a", "Rate breach", Ct);
        containment.DenyRelease = true;

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => engine.ResumeAgentAsync("agent-a", "Cleared", EngineRequest("agent-a", "attacker"), Ct));

        Assert.False(engine.Evaluate("agent-a", ToolEffect.Read).Allowed);
        // The durable stop was never cleared: a restarted engine over the same store still denies the agent.
        var restarted = new KillSwitchEngine(new InMemoryKillSwitchAuditSink(), new RecordingContainment(), inner, _time);
        Assert.Single(restarted.GetStatus().AgentStops);
        Assert.False(restarted.Evaluate("agent-a", ToolEffect.Read).Allowed);
    }

    [Fact]
    public async Task Evaluate_denies_every_effect_until_the_stored_state_is_loaded()
    {
        var load = new TaskCompletionSource();
        var store = new ControllableKillSwitchStateStore(new InMemoryKillSwitchStateStore()) { LoadGate = load.Task };
        var engine = new KillSwitchEngine(new InMemoryKillSwitchAuditSink(), new RecordingContainment(), store, _time);

        var before = engine.Evaluate("agent-a", ToolEffect.Read);
        Assert.False(before.Allowed);
        Assert.Equal(KillSwitchScope.Global, before.Scope);

        load.SetResult();
        await Task.Delay(50, Ct);

        Assert.True(engine.Evaluate("agent-a", ToolEffect.Read).Allowed);
    }

    [Fact]
    public async Task Startup_gate_lets_agents_start_once_a_failed_load_succeeds_on_retry()
    {
        var store = new ControllableKillSwitchStateStore(new InMemoryKillSwitchStateStore()) { FailNextLoad = true };
        var engine = new KillSwitchEngine(new InMemoryKillSwitchAuditSink(), new RecordingContainment(), store, _time);

        await engine.WaitUntilStateLoadedAsync(Ct);

        Assert.Equal(ModuleHealthStatus.Healthy, engine.CheckStateHealth().Status);
    }

    [Fact]
    public async Task Startup_gate_refuses_while_the_stop_store_cannot_be_read()
    {
        var store = new ControllableKillSwitchStateStore(new InMemoryKillSwitchStateStore()) { FailLoads = true };
        var engine = new KillSwitchEngine(new InMemoryKillSwitchAuditSink(), new RecordingContainment(), store, _time);

        await Assert.ThrowsAsync<InvalidOperationException>(() => engine.WaitUntilStateLoadedAsync(Ct));
        Assert.False(engine.Evaluate("agent-a", ToolEffect.Read).Allowed);
        Assert.Equal(ModuleHealthStatus.Failing, engine.CheckStateHealth().Status);

        store.FailLoads = false;
        await engine.WaitUntilStateLoadedAsync(Ct);

        Assert.True(engine.Evaluate("agent-a", ToolEffect.Read).Allowed);
    }

    [Fact]
    public async Task State_health_is_unknown_while_loading_then_healthy_once_loaded()
    {
        var load = new TaskCompletionSource();
        var store = new ControllableKillSwitchStateStore(new InMemoryKillSwitchStateStore()) { LoadGate = load.Task };
        var engine = new KillSwitchEngine(new InMemoryKillSwitchAuditSink(), new RecordingContainment(), store, _time);

        var loading = engine.CheckStateHealth();
        Assert.Equal(ModuleHealthStatus.Unknown, loading.Status);

        load.SetResult();
        await Task.Delay(50, Ct);

        Assert.Equal(ModuleHealthStatus.Healthy, engine.CheckStateHealth().Status);
    }

    [Fact]
    public async Task State_health_is_failing_with_a_cause_when_the_load_fails()
    {
        var store = new ControllableKillSwitchStateStore(new InMemoryKillSwitchStateStore()) { FailNextLoad = true };
        var engine = new KillSwitchEngine(new InMemoryKillSwitchAuditSink(), new RecordingContainment(), store, _time);
        await Task.Delay(50, Ct);

        var health = engine.CheckStateHealth();

        Assert.Equal(ModuleHealthStatus.Failing, health.Status);
        Assert.Contains("failed to load", health.Reason, StringComparison.Ordinal);
        Assert.Contains("effects are denied", health.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_failed_load_is_retried_by_the_next_state_change()
    {
        var store = new ControllableKillSwitchStateStore(new InMemoryKillSwitchStateStore()) { FailNextLoad = true };
        var engine = new KillSwitchEngine(new InMemoryKillSwitchAuditSink(), new RecordingContainment(), store, _time);
        await Task.Delay(50, Ct);

        await engine.EngageGlobalAsync("owner-1", "Freeze", Ct);

        Assert.False(engine.Evaluate("agent-a", ToolEffect.Write).Allowed);
    }

    [Fact]
    public async Task Containment_adapter_recovers_the_contained_set_after_a_restart()
    {
        var store = new InMemoryContainmentStateStore();
        var order = new List<string>();
        var (owners, key) = Owners();
        var first = new AgentContainmentAdapter(new OrderedVmPauser(order), new OrderedTokenRevoker(order), owners, new RecordingAuditLog(), new InMemoryNonceStore(), store);
        await first.ContainAsync("agent-a", "owner-1", Ct);
        order.Clear();

        var restarted = new AgentContainmentAdapter(new OrderedVmPauser(order), new OrderedTokenRevoker(order), owners, new RecordingAuditLog(), new InMemoryNonceStore(), store);
        var result = await ReleaseAsync(restarted, key, "agent-a", Ct);

        Assert.Equal(ContainmentReleaseOutcome.Released, result.Outcome);
        Assert.Equal(["vm-resume:agent-a", "token-resume:agent-a"], order);
        Assert.Equal(ContainmentReleaseOutcome.NotContained, (await ReleaseAsync(restarted, key, "agent-a", Ct)).Outcome);
    }

    [Fact]
    public async Task Containment_adapter_forgets_an_agent_once_it_is_released_in_the_store()
    {
        var store = new InMemoryContainmentStateStore();
        var (owners, key) = Owners();
        var order = new List<string>();
        var first = new AgentContainmentAdapter(new OrderedVmPauser(order), new OrderedTokenRevoker(order), owners, new RecordingAuditLog(), new InMemoryNonceStore(), store);
        await first.ContainAsync("agent-a", "owner-1", Ct);
        await ReleaseAsync(first, key, "agent-a", Ct);

        var restarted = new AgentContainmentAdapter(new OrderedVmPauser(order), new OrderedTokenRevoker(order), owners, new RecordingAuditLog(), new InMemoryNonceStore(), store);

        Assert.Empty(await store.LoadAsync(Ct));
        Assert.Equal(ContainmentReleaseOutcome.NotContained, (await ReleaseAsync(restarted, key, "agent-a", Ct)).Outcome);
    }

    [Fact]
    public async Task The_containment_row_records_the_engaging_actor()
    {
        var store = new InMemoryContainmentStateStore();
        var (owners, _) = Owners();
        var adapter = new AgentContainmentAdapter(new OrderedVmPauser([]), new OrderedTokenRevoker([]), owners, new RecordingAuditLog(), new InMemoryNonceStore(), store);

        await adapter.ContainAsync("agent-a", "owner-1", Ct);
        await adapter.ContainAsync("agent-b", "system:breach", Ct);

        var rows = (await store.LoadAsync(Ct)).ToDictionary(r => r.AgentId, r => r.ActorId);
        Assert.Equal("owner-1", rows["agent-a"]);
        Assert.Equal("system:breach", rows["agent-b"]);
    }

    [Fact]
    public async Task A_failed_containment_write_pauses_and_revokes_nothing()
    {
        var store = new ControllableContainmentStateStore(new InMemoryContainmentStateStore()) { FailWrites = true };
        var order = new List<string>();
        var (owners, key) = Owners();
        var adapter = new AgentContainmentAdapter(new OrderedVmPauser(order), new OrderedTokenRevoker(order), owners, new RecordingAuditLog(), new InMemoryNonceStore(), store);

        await Assert.ThrowsAsync<InvalidOperationException>(() => adapter.ContainAsync("agent-a", "owner-1", Ct));

        Assert.Empty(order);
        Assert.Equal(ContainmentReleaseOutcome.NotContained, (await ReleaseAsync(adapter, key, "agent-a", Ct)).Outcome);
    }

    [Fact]
    public async Task A_failed_containment_removal_keeps_the_agent_contained_in_the_store()
    {
        var store = new ControllableContainmentStateStore(new InMemoryContainmentStateStore());
        var order = new List<string>();
        var (owners, key) = Owners();
        var adapter = new AgentContainmentAdapter(new OrderedVmPauser(order), new OrderedTokenRevoker(order), owners, new RecordingAuditLog(), new InMemoryNonceStore(), store);
        await adapter.ContainAsync("agent-a", "owner-1", Ct);
        store.FailWrites = true;

        await Assert.ThrowsAsync<InvalidOperationException>(() => ReleaseAsync(adapter, key, "agent-a", Ct));

        Assert.Single(await store.LoadAsync(Ct));
    }

    private static (InMemoryOwnerKeyRegistry Owners, ECDsa Key) Owners()
    {
        var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var owners = new InMemoryOwnerKeyRegistry();
        owners.Register(new OwnerDeviceKey("owner-1", "dev-owner", key.ExportSubjectPublicKeyInfo()));
        return (owners, key);
    }

    private static ContainmentReleaseRequest EngineRequest(string agentId, string deviceId) =>
        new(agentId, deviceId, [], DateTimeOffset.MinValue, []);

    /// <summary>Issues a release challenge and releases the agent with a request the owner key signed.</summary>
    private static async Task<ContainmentReleaseResult> ReleaseAsync(AgentContainmentAdapter adapter, ECDsa key, string agentId, CancellationToken ct)
    {
        var challenge = await adapter.IssueReleaseChallengeAsync(agentId, "dev-owner", ct);
        Assert.NotNull(challenge);
        var signature = key.SignData(
            ApprovalSignature.ReleaseMessage(agentId, "dev-owner", challenge.Nonce, challenge.ExpiresAt),
            HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        return await adapter.ReleaseAsync(agentId, new ContainmentReleaseRequest(agentId, "dev-owner", challenge.Nonce, challenge.ExpiresAt, signature), ct);
    }
}
