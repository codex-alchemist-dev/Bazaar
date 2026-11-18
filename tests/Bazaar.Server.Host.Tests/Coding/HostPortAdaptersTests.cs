using Bazaar.Contracts.Coding;
using Bazaar.Server.CodingGit;
using Bazaar.Server.CodingPermissions;
using Bazaar.Server.Host.Coding;
using Bazaar.Server.Security.KillSwitch;
using Bazaar.Server.Subagents;
using Microsoft.Extensions.DependencyInjection;

namespace Bazaar.Server.Host.Tests.Coding;

public sealed class HostPortAdaptersTests
{
    private static ToolCallRecord Row() =>
        new(Guid.NewGuid(), "s", "t", "Read", "agent", false, ToolCallStatus.Unknown, null, new string('a', 64), DateTimeOffset.UtcNow, null);

    [Fact]
    public async Task The_tool_call_trace_appends_in_order_and_fails_closed_when_full()
    {
        var trace = new InProcessToolCallTrace(capacity: 2);
        var first = Row();
        var second = Row();

        await trace.AppendAsync(first, CancellationToken.None);
        await trace.AppendAsync(second, CancellationToken.None);

        Assert.Equal([first, second], trace.Rows);
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await trace.AppendAsync(Row(), CancellationToken.None));
        Assert.Equal(2, trace.Rows.Count);
    }

    [Fact]
    public async Task The_tool_call_trace_completes_a_row_once()
    {
        var trace = new InProcessToolCallTrace();
        var row = Row();
        await trace.AppendAsync(row, CancellationToken.None);

        Assert.True(await trace.CompleteAsync(row.Id, ToolCallStatus.Succeeded, 0, CancellationToken.None));
        Assert.False(await trace.CompleteAsync(row.Id, ToolCallStatus.Failed, 1, CancellationToken.None));
        Assert.False(await trace.CompleteAsync(Guid.NewGuid(), ToolCallStatus.Failed, 1, CancellationToken.None));
        var done = Assert.Single(trace.Rows);
        Assert.Equal(ToolCallStatus.Succeeded, done.Status);
        Assert.Equal(0, done.ExitCode);
        Assert.NotNull(done.EndedAt);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () =>
            await trace.CompleteAsync(row.Id, ToolCallStatus.Unknown, null, CancellationToken.None));
    }

    [Fact]
    public async Task The_subagent_trace_appends_and_fails_closed_when_full()
    {
        var trace = new InProcessSubagentTrace(capacity: 1);
        var entry = new SubagentTraceEntry(
            SubagentTraceKind.Spawned, "sub-1", "parent", "chain", 1, "model", ["read"], WorktreeRequest.None, [], DateTimeOffset.UtcNow);

        await trace.AppendAsync(entry, CancellationToken.None);

        Assert.Equal([entry], trace.Rows);
        await Assert.ThrowsAsync<InvalidOperationException>(() => trace.AppendAsync(entry, CancellationToken.None));
    }

    private sealed class NoopContainment : IAgentContainment
    {
        public Task ContainAsync(string agentId, string actorId, CancellationToken ct = default) => Task.CompletedTask;

        public Task<ReleaseChallenge?> IssueReleaseChallengeAsync(string agentId, string deviceId, CancellationToken ct = default) =>
            Task.FromResult<ReleaseChallenge?>(null);

        public Task<ContainmentReleaseResult> ReleaseAsync(string agentId, ContainmentReleaseRequest request, CancellationToken ct = default) =>
            Task.FromResult(new ContainmentReleaseResult(ContainmentReleaseOutcome.Denied));
    }

    private static async Task<KillSwitchEngine> LoadedEngineAsync()
    {
        var engine = new KillSwitchEngine(new InMemoryKillSwitchAuditSink(), new NoopContainment());
        await engine.WaitUntilStateLoadedAsync(TestContext.Current.CancellationToken);
        return engine;
    }

    [Fact]
    public async Task Freeze_engages_an_agent_stop_that_IsFrozen_reports_and_other_agents_stay_free()
    {
        var engine = await LoadedEngineAsync();
        var freeze = new KillSwitchAgentFreeze(engine);

        Assert.False(await freeze.IsFrozenAsync("agent-1", CancellationToken.None));
        await freeze.FreezeAsync("agent-1", "Rate cap breached.", CancellationToken.None);

        Assert.True(await freeze.IsFrozenAsync("agent-1", CancellationToken.None));
        Assert.False(await freeze.IsFrozenAsync("agent-2", CancellationToken.None));
        var stop = Assert.Single(engine.GetStatus().AgentStops);
        Assert.Equal(KillSwitchEngine.BreachActorId, stop.ActorId);
    }

    [Fact]
    public async Task A_global_stop_alone_does_not_freeze_an_agent_because_reads_stay_allowed()
    {
        var engine = await LoadedEngineAsync();
        await engine.EngageGlobalAsync("owner-device", "drill", TestContext.Current.CancellationToken);

        Assert.False(await new KillSwitchAgentFreeze(engine).IsFrozenAsync("agent-1", CancellationToken.None));
    }

    private sealed class RecordingGate(PermissionDecision decision) : IToolPermissionGate
    {
        public ToolCallContext? Seen { get; private set; }

        public ValueTask<PermissionDecision> EvaluateAsync(ToolCallContext context, CancellationToken cancellationToken)
        {
            Seen = context;
            return ValueTask.FromResult(decision);
        }
    }

    private sealed class FixedSessions(PushSession? session) : IPushSessionDirectory
    {
        public ValueTask<PushSession?> FindAsync(PushGateRequest request, CancellationToken cancellationToken) => ValueTask.FromResult(session);
    }

    private static readonly PushGateRequest _push = new("agent-1", new RepoRef("repo1", "main"), BranchName.ForTask("fix-x"), "gc-1", "fix-x");

    private static PushSession Session(SessionTaint taint = SessionTaint.None) =>
        new(Guid.NewGuid().ToString("D"), taint, ToolSessionRole.Actor, "agent");

    [Fact]
    public async Task A_push_is_judged_as_a_push_class_call_on_the_branch_with_the_sessions_taint()
    {
        var gate = new RecordingGate(PermissionDecision.Allow());
        var port = new PermissionGatePushGate(gate, new FixedSessions(Session(SessionTaint.Soft)));

        var decision = await port.EvaluateAsync(_push, CancellationToken.None);

        Assert.Equal(PushVerdict.Allow, decision.Verdict);
        var seen = gate.Seen!;
        Assert.Equal(ToolCapabilityClass.Push, seen.CapabilityClass);
        Assert.Equal("agent/fix-x", seen.TargetBranch);
        Assert.Equal(SessionTaint.Soft, seen.Taint);
        Assert.Equal(ToolSessionRole.Actor, seen.Role);
        Assert.Equal(64, seen.ArgsSha256.Length);
        Assert.NotEqual(System.Text.Json.JsonValueKind.Undefined, seen.Args.ValueKind);
    }

    [Fact]
    public async Task Gate_outcomes_map_to_push_verdicts()
    {
        var scope = new ApprovalScope("push", PerAction: true);
        var needs = new PermissionGatePushGate(new RecordingGate(PermissionDecision.RequireApproval("hard taint", scope)), new FixedSessions(Session()));
        var deny = new PermissionGatePushGate(new RecordingGate(PermissionDecision.Deny("frozen")), new FixedSessions(Session()));

        Assert.Equal(PushVerdict.NeedsApproval, (await needs.EvaluateAsync(_push, CancellationToken.None)).Verdict);
        var denied = await deny.EvaluateAsync(_push, CancellationToken.None);
        Assert.Equal(PushVerdict.Deny, denied.Verdict);
        Assert.Equal("frozen", denied.Reason);
    }

    [Fact]
    public async Task A_push_without_a_running_session_is_denied_and_never_reaches_the_gate()
    {
        var gate = new RecordingGate(PermissionDecision.Allow());
        var port = new PermissionGatePushGate(gate, new FixedSessions(null));

        var decision = await port.EvaluateAsync(_push, CancellationToken.None);

        Assert.Equal(PushVerdict.Deny, decision.Verdict);
        Assert.Null(gate.Seen);
    }

    [Fact]
    public void The_harness_registers_the_sinks_and_ports_with_a_server_source_and_leaves_the_rest_failing_closed()
    {
        var services = new ServiceCollection();
        services.AddBazaarCodingHarness();
        services.AddSingleton(new KillSwitchEngine(new InMemoryKillSwitchAuditSink(), new NoopContainment()));
        using var provider = services.BuildServiceProvider();

        Assert.IsType<InProcessToolCallTrace>(provider.GetRequiredService<IToolCallTrace>());
        Assert.IsType<InProcessSubagentTrace>(provider.GetRequiredService<ISubagentTrace>());
        Assert.IsType<KillSwitchAgentFreeze>(provider.GetRequiredService<IAgentFreeze>());

        // No Server source exists for these, so they must not resolve.
        Assert.Throws<InvalidOperationException>(provider.GetRequiredService<IAgentGitIdentity>);
        Assert.Throws<InvalidOperationException>(provider.GetRequiredService<IWorkShareLayout>);
        Assert.Throws<InvalidOperationException>(provider.GetRequiredService<IRuntimeSubagentParentDirectory>);
        Assert.Throws<InvalidOperationException>(provider.GetRequiredService<IPushGate>);
    }
}
