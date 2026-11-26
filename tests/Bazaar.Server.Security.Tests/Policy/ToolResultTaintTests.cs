using Bazaar.Server.Security.Policy;
using Bazaar.Server.Security.Taint;
using static Bazaar.Server.Security.Tests.Policy.PolicyFixtures;

namespace Bazaar.Server.Security.Tests.Policy;

public class ToolResultTaintTests
{
    private static readonly Guid _resultId = Guid.Parse("0196a6f0-0000-7000-8000-000000000b01");

    private static readonly ToolCapability _capture = new(
        "screen.capture", ToolTier.Autonomous, CapabilityClass.Read, SessionRoles.Reader | SessionRoles.Actor, ResultTaint: ToolResultTaint.Hard);

    private static ToolPolicyEngine Engine() =>
        new(CapabilityRegistry.Create([_read, _tier2, _capture]), new InMemoryRateBucketStore(), PolicyOptions.Default);

    [Fact]
    public void A_capability_declares_no_result_taint_by_default()
    {
        Assert.Equal(ToolResultTaint.None, _read.ResultTaint);
    }

    [Fact]
    public void The_registry_rejects_an_undefined_result_taint()
    {
        var bad = _read with { ResultTaint = (ToolResultTaint)9 };

        Assert.Throws<ArgumentException>(() => CapabilityRegistry.Create([bad]));
    }

    [Fact]
    public void The_registry_keeps_the_declared_result_taint()
    {
        var registry = CapabilityRegistry.Create([_capture]);

        Assert.True(registry.TryGet("screen.capture", out var tool));
        Assert.Equal(ToolResultTaint.Hard, tool.ResultTaint);
    }

    [Fact]
    public void A_hard_result_taints_a_clean_session_hard_with_the_result_as_an_external_source()
    {
        var after = Engine().TaintAfterResult(Context("screen.capture"), _resultId);

        Assert.Equal(TaintState.Hard, after.State);
        var source = Assert.Single(after.Sources);
        Assert.Equal(_resultId, source.SourceId);
        Assert.Equal(ContentTrust.External, source.Trust);
        Assert.Equal(TaintState.Hard, source.Raised);
    }

    [Fact]
    public void A_hard_result_raises_a_soft_session_and_keeps_its_sources()
    {
        var soft = Soft();

        var after = Engine().TaintAfterResult(Context("screen.capture", taint: soft), _resultId);

        Assert.Equal(TaintState.Hard, after.State);
        Assert.Equal(2, after.Sources.Length);
        Assert.Equal(soft.Sources[0], after.Sources[0]);
    }

    [Fact]
    public void The_same_result_is_recorded_once()
    {
        var engine = Engine();
        var once = engine.TaintAfterResult(Context("screen.capture"), _resultId);

        var twice = engine.TaintAfterResult(Context("screen.capture", taint: once), _resultId);

        Assert.Single(twice.Sources);
    }

    [Theory]
    [InlineData(TaintState.Clean)]
    [InlineData(TaintState.Soft)]
    [InlineData(TaintState.Hard)]
    public void A_tool_without_a_declaration_leaves_the_taint_unchanged(TaintState state)
    {
        var before = TaintAt(state);

        var after = Engine().TaintAfterResult(Context("gc.read", taint: before), _resultId);

        Assert.Same(before, after);
    }

    [Fact]
    public void An_unknown_tool_counts_as_hard()
    {
        var after = Engine().TaintAfterResult(Context("not.registered"), _resultId);

        Assert.Equal(TaintState.Hard, after.State);
    }

    [Fact]
    public void A_null_context_is_rejected()
    {
        Assert.Throws<ArgumentNullException>(() => Engine().TaintAfterResult(null!, _resultId));
    }

    [Fact]
    public void The_declaration_does_not_change_the_call_decision()
    {
        var engine = Engine();

        Assert.Equal(new PolicyDecision(PolicyOutcome.Allow, PolicyReason.Autonomous), engine.Evaluate(Context("screen.capture")));
        Assert.Equal(new PolicyDecision(PolicyOutcome.Allow, PolicyReason.Autonomous), engine.Evaluate(Context("screen.capture", SessionRoles.Reader, Hard())));
    }

    [Fact]
    public void The_interface_default_fails_closed_for_engines_without_declarations()
    {
        IToolPolicyEngine engine = new DecisionOnlyEngine();

        var after = engine.TaintAfterResult(Context("gc.read"), _resultId);

        Assert.Equal(TaintState.Hard, after.State);
    }

    private sealed class DecisionOnlyEngine : IToolPolicyEngine
    {
        public PolicyDecision Evaluate(DispatchContext context) => new(PolicyOutcome.Deny, PolicyReason.UnknownTool);
    }
}
