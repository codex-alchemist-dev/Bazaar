using Bazaar.Contracts.Gateway;
using Bazaar.Server.Agents;
using Bazaar.Server.Agents.Turns;
using Bazaar.Server.Agents.Turns.Tools;
using Bazaar.Server.Security.Policy;
using Bazaar.Server.Security.Taint;
using Bazaar.Server.Tests.Security.Policy;

namespace Bazaar.Server.Tests.Agents.Turns.Tools;

/// <summary>
/// The taint gate runs through the real <see cref="ToolPolicyEngine"/> (its decision table is the one place the gate is
/// enforced), so these tests check the dispatcher's behaviour on the engine's real answers.
/// </summary>
public class ToolDispatcherTaintTests
{
    private static readonly Guid _agentId = Guid.Parse("0196a6f0-0000-7000-8000-000000000501");

    private static readonly Guid _turnId = Guid.Parse("0196a6f0-0000-7000-8000-000000000502");

    private static readonly Guid _sessionId = Guid.Parse("0196a6f0-0000-7000-8000-000000000503");

    private static ToolDispatcher Dispatcher(SessionTaint taint, FakeTool tool, string toolName, PolicyOptions? options = null) =>
        new(
            new FakeToolRegistry((toolName, tool)),
            new ToolPolicyEngine(PolicyFixtures.Standard(), new InMemoryRateBucketStore(), options ?? PolicyOptions.Default),
            new FakeSessionTaintStore(taint),
            new FakeInFlightCallStore(),
            new FakeAuditLog(),
            new FixedClock(PolicyFixtures._now));

    private static ToolDispatchRequest Request(string tool) =>
        new(_agentId, _turnId, _sessionId, TurnRole.Actor, new ModelToolCall("call-1", tool, "{}"), Idempotent: false);

    [Fact]
    public async Task A_hard_tainted_session_gets_pending_approval_for_a_tier_2_tool()
    {
        var tool = new FakeTool(_ => Task.FromResult("{}"));

        var result = await Dispatcher(PolicyFixtures.Hard(), tool, "repo.push")
            .InvokeAsync(Request("repo.push"), CancellationToken.None);

        Assert.Equal(ToolDispatchOutcome.PendingApproval, result.Outcome);
        Assert.Equal("HardTaintTier2", result.Reason);
        Assert.Equal(0, tool.RunCount);
    }

    [Fact]
    public async Task Hard_taint_escalates_a_tier_1_tool_to_pending_approval()
    {
        var tool = new FakeTool(_ => Task.FromResult("{}"));

        var result = await Dispatcher(PolicyFixtures.Hard(), tool, "mail.reply")
            .InvokeAsync(Request("mail.reply"), CancellationToken.None);

        Assert.Equal(ToolDispatchOutcome.PendingApproval, result.Outcome);
        Assert.Equal(0, tool.RunCount);
    }

    [Fact]
    public async Task A_soft_tainted_session_does_not_gate_a_tier_2_tool_when_the_setting_is_hard_taint_only()
    {
        var tool = new FakeTool(_ => Task.FromResult("{}"));
        var options = new PolicyOptions(Tier2WhenUntainted.HardTaintOnly);

        var result = await Dispatcher(PolicyFixtures.Soft(), tool, "repo.push", options)
            .InvokeAsync(Request("repo.push"), CancellationToken.None);

        Assert.Equal(ToolDispatchOutcome.Executed, result.Outcome);
        Assert.Equal(1, tool.RunCount);
    }

    [Fact]
    public async Task A_soft_tainted_session_does_not_escalate_a_tier_1_tool()
    {
        var tool = new FakeTool(_ => Task.FromResult("{}"));

        var result = await Dispatcher(PolicyFixtures.Soft(), tool, "mail.reply")
            .InvokeAsync(Request("mail.reply"), CancellationToken.None);

        Assert.Equal(ToolDispatchOutcome.Executed, result.Outcome);
        Assert.Equal(1, tool.RunCount);
    }

    // Result taint (doc 15, "Reads for modules"): the dispatcher saves the engine's TaintAfterResult when it raises the taint.

    private static readonly ToolCapability _capture = new(
        "screen.capture", ToolTier.Autonomous, CapabilityClass.Read, SessionRoles.Reader | SessionRoles.Actor, ResultTaint: ToolResultTaint.Hard);

    private sealed record Rig(ToolDispatcher Dispatcher, FakeSessionTaintStore Store, FakeAuditLog Audit, FakeInFlightCallStore InFlight);

    private static Rig ResultRig(SessionTaint taint, FakeTool tool, string toolName)
    {
        var store = new FakeSessionTaintStore(taint);
        var audit = new FakeAuditLog();
        var inFlight = new FakeInFlightCallStore();
        var dispatcher = new ToolDispatcher(
            new FakeToolRegistry((toolName, tool)),
            new ToolPolicyEngine(CapabilityRegistry.Create([PolicyFixtures._read, _capture]), new InMemoryRateBucketStore(), PolicyOptions.Default),
            store,
            inFlight,
            audit,
            new FixedClock(PolicyFixtures._now));
        return new Rig(dispatcher, store, audit, inFlight);
    }

    [Fact]
    public async Task A_tool_that_declares_hard_result_taint_saves_hard_taint_for_the_session()
    {
        var rig = ResultRig(SessionTaint.Clean, new FakeTool(_ => Task.FromResult("""{"frame":1}""")), "screen.capture");

        var result = await rig.Dispatcher.InvokeAsync(Request("screen.capture"), TestContext.Current.CancellationToken);

        Assert.Equal(ToolDispatchOutcome.Executed, result.Outcome);
        Assert.Equal("""{"frame":1}""", result.ResultJson);
        var (sessionId, saved) = Assert.Single(rig.Store.Saved);
        Assert.Equal(_sessionId, sessionId);
        Assert.Equal(TaintState.Hard, saved.State);
        var source = Assert.Single(saved.Sources);
        Assert.Equal(ContentTrust.External, source.Trust);
        Assert.Equal(TaintState.Hard, source.Raised);
        Assert.Contains("\"reason\":\"Autonomous\"", Assert.Single(rig.Audit.Entries).Payload);
    }

    [Fact]
    public async Task A_hard_result_in_a_hard_session_adds_its_source_to_the_lineage()
    {
        var rig = ResultRig(PolicyFixtures.Hard(), new FakeTool(_ => Task.FromResult("{}")), "screen.capture");

        await rig.Dispatcher.InvokeAsync(Request("screen.capture"), TestContext.Current.CancellationToken);

        var (_, saved) = Assert.Single(rig.Store.Saved);
        Assert.Equal(TaintState.Hard, saved.State);
        Assert.Equal(2, saved.Sources.Length);
        Assert.Equal(PolicyFixtures._hardSourceId, saved.Sources[0].SourceId);
    }

    [Theory]
    [InlineData(TaintState.Clean)]
    [InlineData(TaintState.Soft)]
    [InlineData(TaintState.Hard)]
    public async Task A_tool_without_a_result_taint_saves_nothing_and_returns_its_result(TaintState state)
    {
        var rig = ResultRig(PolicyFixtures.TaintAt(state), new FakeTool(_ => Task.FromResult("""{"ok":true}""")), "gc.read");

        var result = await rig.Dispatcher.InvokeAsync(Request("gc.read"), TestContext.Current.CancellationToken);

        Assert.Equal(ToolDispatchOutcome.Executed, result.Outcome);
        Assert.Equal("""{"ok":true}""", result.ResultJson);
        Assert.Empty(rig.Store.Saved);
        Assert.Contains("\"reason\":\"Autonomous\"", Assert.Single(rig.Audit.Entries).Payload);
    }

    [Fact]
    public async Task A_hard_result_tool_that_throws_adds_no_taint()
    {
        var rig = ResultRig(SessionTaint.Clean, new FakeTool(_ => throw new InvalidOperationException("boom")), "screen.capture");

        var result = await rig.Dispatcher.InvokeAsync(Request("screen.capture"), TestContext.Current.CancellationToken);

        Assert.Equal("""{"error":"tool failed"}""", result.ResultJson);
        Assert.Empty(rig.Store.Saved);
    }

    [Fact]
    public async Task A_failed_taint_save_withholds_the_result()
    {
        var rig = ResultRig(SessionTaint.Clean, new FakeTool(_ => Task.FromResult("""{"frame":"secret"}""")), "screen.capture");
        rig.Store.SaveFailure = new InvalidOperationException("store down");

        var result = await rig.Dispatcher.InvokeAsync(Request("screen.capture"), TestContext.Current.CancellationToken);

        Assert.Equal(ToolDispatchOutcome.Executed, result.Outcome);
        Assert.Equal("""{"error":"tool failed"}""", result.ResultJson);
        var entry = Assert.Single(rig.Audit.Entries);
        Assert.Contains("\"reason\":\"taint_save_failed:InvalidOperationException\"", entry.Payload);
        Assert.DoesNotContain("store down", entry.Payload);
        Assert.Equal(0, rig.InFlight.OpenCount);
    }

    [Fact]
    public async Task A_cancelled_taint_save_writes_one_audit_record_and_rethrows()
    {
        var rig = ResultRig(SessionTaint.Clean, new FakeTool(_ => Task.FromResult("{}")), "screen.capture");
        rig.Store.SaveFailure = new OperationCanceledException();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => rig.Dispatcher.InvokeAsync(Request("screen.capture"), TestContext.Current.CancellationToken));

        var entry = Assert.Single(rig.Audit.Entries);
        Assert.Contains("\"outcome\":\"Executed\"", entry.Payload);
        Assert.Contains("\"reason\":\"cancelled\"", entry.Payload);
        Assert.Empty(rig.Store.Saved);
    }

    [Fact]
    public async Task The_engine_gets_the_evaluated_context_and_a_fresh_result_id()
    {
        var engine = new FakePolicyEngine(new PolicyDecision(PolicyOutcome.Allow, PolicyReason.Autonomous));
        var seen = new List<(DispatchContext Context, Guid ResultId)>();
        engine.AfterResult = (context, resultId) =>
        {
            seen.Add((context, resultId));
            return context.Taint;
        };
        var store = new FakeSessionTaintStore(SessionTaint.Clean);
        var dispatcher = new ToolDispatcher(
            new FakeToolRegistry(("gc.read", new FakeTool(_ => Task.FromResult("{}")))),
            engine,
            store,
            new FakeInFlightCallStore(),
            new FakeAuditLog(),
            new FixedClock(PolicyFixtures._now));

        await dispatcher.InvokeAsync(Request("gc.read"), TestContext.Current.CancellationToken);

        var (context, resultId) = Assert.Single(seen);
        Assert.Same(Assert.Single(engine.Seen), context);
        Assert.NotEqual(Guid.Empty, resultId);
        Assert.Empty(store.Saved);
    }
}
