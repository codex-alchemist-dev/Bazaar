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
}
