using Bazaar.Server.Security.Policy;
using Bazaar.Server.Security.Taint;

namespace Bazaar.Server.Tests.Security.Policy;

public class ToolPolicyEngineTests
{
    private static readonly Guid _approvalId = Guid.Parse("0196a6f0-0000-7000-8000-000000000301");

    private const string _target = "codex-alchemist-dev/Bazaar";

    private const string _args = """{"branch":"agent/x","force":false}""";

    private static ToolPolicyEngine Engine(PolicyOptions? options = null) =>
        new(PolicyFixtures.Standard(), new InMemoryRateBucketStore(), options ?? PolicyOptions.Default);

    private static PolicyDecision Decide(string tool, SessionRoles role = SessionRoles.Actor, SessionTaint? taint = null, PolicyOptions? options = null, string args = "{}") =>
        Engine(options).Evaluate(PolicyFixtures.Context(tool, role, taint, args: args));

    /// <summary>A grant for the <c>repo.push</c> call, hashed the way the engine will hash it. Override any part to break the match.</summary>
    private static VerifiedApproval Grant(
        string agent = "agent-a",
        string tool = "repo.push",
        string? target = _target,
        string args = _args,
        SessionTaint? taint = null,
        DateTimeOffset? expiresAt = null) =>
        new(
            _approvalId,
            agent,
            tool,
            DispatchHash.Compute(tool, target, PolicyFixtures.Args(args), (taint ?? PolicyFixtures.Hard()).Sources),
            expiresAt ?? PolicyFixtures._now.AddMinutes(30));

    private static PolicyDecision DecideWithGrant(VerifiedApproval grant, SessionTaint taint) =>
        Engine().Evaluate(PolicyFixtures.Context("repo.push", taint: taint, target: _target, args: _args, grant: grant));

    [Fact]
    public void An_unknown_tool_is_denied()
    {
        var decision = Decide("does.not.exist");

        Assert.Equal(PolicyOutcome.Deny, decision.Outcome);
        Assert.Equal(PolicyReason.UnknownTool, decision.Reason);
    }

    [Fact]
    public void A_reader_only_tool_called_from_an_actor_is_denied()
    {
        var decision = Decide("web.fetch", SessionRoles.Actor);

        Assert.Equal(PolicyOutcome.Deny, decision.Outcome);
        Assert.Equal(PolicyReason.RoleNotPermitted, decision.Reason);
    }

    [Fact]
    public void An_actor_only_tool_called_from_a_reader_is_denied()
    {
        var decision = Decide("gc.post", SessionRoles.Reader);

        Assert.Equal(PolicyOutcome.Deny, decision.Outcome);
        Assert.Equal(PolicyReason.RoleNotPermitted, decision.Reason);
    }

    [Fact]
    public void A_tool_allowed_for_both_roles_runs_from_either()
    {
        Assert.Equal(PolicyOutcome.Allow, Decide("gc.read", SessionRoles.Reader).Outcome);
        Assert.Equal(PolicyOutcome.Allow, Decide("gc.read", SessionRoles.Actor).Outcome);
    }

    [Fact]
    public void A_denied_tier_tool_is_denied()
    {
        var decision = Decide("secret.export");

        Assert.Equal(PolicyOutcome.Deny, decision.Outcome);
        Assert.Equal(PolicyReason.TierDenied, decision.Reason);
    }

    [Fact]
    public void A_human_performs_tool_is_denied_for_an_agent()
    {
        var decision = Decide("account.create");

        Assert.Equal(PolicyOutcome.Deny, decision.Outcome);
        Assert.Equal(PolicyReason.HumanPerformsAction, decision.Reason);
    }

    [Fact]
    public void An_autonomous_tool_is_allowed()
    {
        var decision = Decide("gc.read");

        Assert.Equal(PolicyOutcome.Allow, decision.Outcome);
        Assert.Equal(PolicyReason.Autonomous, decision.Reason);
    }

    [Theory]
    [InlineData(TaintState.Clean)]
    [InlineData(TaintState.Soft)]
    public void An_autonomous_with_notice_tool_is_allowed_with_a_notice_when_clean_or_soft(TaintState state)
    {
        var taint = PolicyFixtures.TaintAt(state);
        Assert.Equal(state, taint.State);

        var decision = Decide("mail.reply", taint: taint);

        Assert.Equal(PolicyOutcome.Allow, decision.Outcome);
        Assert.Equal(PolicyReason.NoticeRequired, decision.Reason);
    }

    [Fact]
    public void An_autonomous_with_notice_tool_escalates_to_approval_under_hard_taint()
    {
        var decision = Decide("mail.reply", taint: PolicyFixtures.Hard());

        Assert.Equal(PolicyOutcome.RequireApproval, decision.Outcome);
        Assert.Equal(PolicyReason.HardTaintTier2, decision.Reason);
    }

    [Fact]
    public void A_tier_two_tool_in_a_clean_session_requires_approval_by_default()
    {
        var decision = Decide("repo.push");

        Assert.Equal(PolicyOutcome.RequireApproval, decision.Outcome);
        Assert.Equal(PolicyReason.OwnerApprovalRequired, decision.Reason);
    }

    [Theory]
    [InlineData(TaintState.Clean)]
    [InlineData(TaintState.Soft)]
    public void Hard_taint_only_setting_allows_tier_two_when_clean_or_soft(TaintState state)
    {
        var options = new PolicyOptions(Tier2WhenUntainted.HardTaintOnly);

        var decision = Decide("repo.push", taint: PolicyFixtures.TaintAt(state), options: options);

        Assert.Equal(PolicyOutcome.Allow, decision.Outcome);
        Assert.Equal(PolicyReason.UntaintedTier2Allowed, decision.Reason);
    }

    [Fact]
    public void Hard_taint_only_setting_still_requires_approval_for_tier_two_under_hard_taint()
    {
        var options = new PolicyOptions(Tier2WhenUntainted.HardTaintOnly);

        var decision = Decide("repo.push", taint: PolicyFixtures.Hard(), options: options);

        Assert.Equal(PolicyOutcome.RequireApproval, decision.Outcome);
        Assert.Equal(PolicyReason.HardTaintTier2, decision.Reason);
    }

    [Fact]
    public void A_verified_grant_for_the_exact_call_allows_tier_two_under_hard_taint()
    {
        var hard = PolicyFixtures.Hard();

        var decision = DecideWithGrant(Grant(taint: hard), hard);

        Assert.Equal(PolicyOutcome.Allow, decision.Outcome);
        Assert.Equal(PolicyReason.ApprovedByGrant, decision.Reason);
    }

    [Fact]
    public void A_verified_grant_for_the_exact_call_allows_tier_two_in_a_clean_session()
    {
        var decision = DecideWithGrant(Grant(taint: SessionTaint.Clean), SessionTaint.Clean);

        Assert.Equal(PolicyOutcome.Allow, decision.Outcome);
        Assert.Equal(PolicyReason.ApprovedByGrant, decision.Reason);
    }

    [Fact]
    public void A_grant_for_another_agent_does_not_allow()
    {
        var hard = PolicyFixtures.Hard();

        var decision = DecideWithGrant(Grant(agent: "agent-b", taint: hard), hard);

        Assert.Equal(PolicyOutcome.RequireApproval, decision.Outcome);
        Assert.Equal(PolicyReason.HardTaintTier2, decision.Reason);
    }

    [Fact]
    public void A_grant_for_another_tool_does_not_allow()
    {
        var hard = PolicyFixtures.Hard();

        var decision = DecideWithGrant(Grant(tool: "repo.force", taint: hard), hard);

        Assert.Equal(PolicyOutcome.RequireApproval, decision.Outcome);
        Assert.Equal(PolicyReason.HardTaintTier2, decision.Reason);
    }

    [Fact]
    public void A_grant_for_other_args_does_not_allow()
    {
        var hard = PolicyFixtures.Hard();

        var decision = DecideWithGrant(Grant(args: """{"branch":"main","force":true}""", taint: hard), hard);

        Assert.Equal(PolicyOutcome.RequireApproval, decision.Outcome);
        Assert.Equal(PolicyReason.HardTaintTier2, decision.Reason);
    }

    [Fact]
    public void A_grant_for_another_target_does_not_allow()
    {
        var hard = PolicyFixtures.Hard();

        var decision = DecideWithGrant(Grant(target: "codex-alchemist-dev/Other", taint: hard), hard);

        Assert.Equal(PolicyOutcome.RequireApproval, decision.Outcome);
        Assert.Equal(PolicyReason.HardTaintTier2, decision.Reason);
    }

    [Fact]
    public void A_grant_made_under_other_taint_sources_does_not_allow()
    {
        var hard = PolicyFixtures.Hard();
        var grant = Grant(taint: PolicyFixtures.Hard(PolicyFixtures._otherHardSourceId));

        var decision = DecideWithGrant(grant, hard);

        Assert.Equal(PolicyOutcome.RequireApproval, decision.Outcome);
        Assert.Equal(PolicyReason.HardTaintTier2, decision.Reason);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void An_expired_grant_does_not_allow(int secondsFromNow)
    {
        var hard = PolicyFixtures.Hard();
        var grant = Grant(taint: hard, expiresAt: PolicyFixtures._now.AddSeconds(secondsFromNow));

        var decision = DecideWithGrant(grant, hard);

        Assert.Equal(PolicyOutcome.RequireApproval, decision.Outcome);
        Assert.Equal(PolicyReason.HardTaintTier2, decision.Reason);
    }

    [Fact]
    public void A_grant_with_a_wrong_args_hash_does_not_allow()
    {
        var hard = PolicyFixtures.Hard();
        var grant = Grant(taint: hard) with { ArgsHash = new byte[DispatchHash.Length] };

        var decision = DecideWithGrant(grant, hard);

        Assert.Equal(PolicyOutcome.RequireApproval, decision.Outcome);
        Assert.Equal(PolicyReason.HardTaintTier2, decision.Reason);
    }

    [Fact]
    public void A_grant_with_a_truncated_args_hash_does_not_allow()
    {
        var hard = PolicyFixtures.Hard();
        var full = Grant(taint: hard).ArgsHash;
        var grant = Grant(taint: hard) with { ArgsHash = full[..(DispatchHash.Length - 1)] };

        var decision = DecideWithGrant(grant, hard);

        Assert.Equal(PolicyOutcome.RequireApproval, decision.Outcome);
        Assert.Equal(PolicyReason.HardTaintTier2, decision.Reason);
    }

    [Fact]
    public void Arguments_with_a_repeated_property_are_denied_even_for_an_autonomous_tool()
    {
        var decision = Decide("gc.read", args: """{"path":"a","path":"b"}""");

        Assert.Equal(PolicyOutcome.Deny, decision.Outcome);
        Assert.Equal(PolicyReason.MalformedArguments, decision.Reason);
    }

    [Fact]
    public void Arguments_with_a_nested_repeated_property_are_denied()
    {
        var decision = Decide("gc.read", args: """{"outer":{"k":1,"k":2}}""");

        Assert.Equal(PolicyOutcome.Deny, decision.Outcome);
        Assert.Equal(PolicyReason.MalformedArguments, decision.Reason);
    }
}
