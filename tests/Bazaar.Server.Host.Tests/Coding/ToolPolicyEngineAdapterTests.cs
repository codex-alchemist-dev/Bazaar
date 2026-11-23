using System.Text.Json;
using Bazaar.Server.CodingPermissions;
using Bazaar.Server.Host.Coding;
using Bazaar.Server.Security.Policy;

namespace Bazaar.Server.Host.Tests.Coding;

public sealed class ToolPolicyEngineAdapterTests
{
    private static readonly string _session = Guid.NewGuid().ToString("D");
    private static readonly string _hash = new('a', 64);

    private static readonly ToolCapability _read = new("code.read", ToolTier.Autonomous, CapabilityClass.Read, SessionRoles.Reader | SessionRoles.Actor);
    private static readonly ToolCapability _exec = new("code.exec", ToolTier.AutonomousWithNotice, CapabilityClass.Exec, SessionRoles.Actor);
    private static readonly ToolCapability _push = new("code.push", ToolTier.OwnerApproves, CapabilityClass.Push, SessionRoles.Actor);
    private static readonly ToolCapability _limited = new("code.limited", ToolTier.Autonomous, CapabilityClass.Read, SessionRoles.Actor, new RateRule(1, TimeSpan.FromHours(1)));

    private static ToolPolicyEngineAdapter Make(Tier2WhenUntainted tier2 = Tier2WhenUntainted.Always)
    {
        var registry = CapabilityRegistry.Create([_read, _exec, _push, _limited]);
        var engine = new ToolPolicyEngine(registry, new InMemoryRateBucketStore(), new PolicyOptions(tier2));
        return new ToolPolicyEngineAdapter(engine, registry);
    }

    private static JsonElement Args(string json = "{}") => JsonElement.Parse(json);

    private static ToolCallContext Ctx(
        string tool,
        ToolCapabilityClass cls,
        ToolSessionRole role = ToolSessionRole.Actor,
        CodingPermissions.SessionTaint taint = CodingPermissions.SessionTaint.None,
        string? branch = null,
        JsonElement? args = null,
        ToolApprovalGrant? grant = null,
        string? session = null) =>
        new("agent-1", session ?? _session, tool, cls, _hash, "agent", taint, null, branch, args ?? Args(), role, grant);

    [Fact]
    public void A_read_tool_in_an_actor_session_is_allowed()
    {
        var decision = Make().Evaluate(Ctx("code.read", ToolCapabilityClass.Read));

        Assert.Equal(PermissionOutcome.Allow, decision.Outcome);
        Assert.False(decision.ApprovedByGrant);
        Assert.False(decision.RateLimited);
    }

    [Fact]
    public void A_reader_session_cannot_call_an_actor_only_tool()
    {
        var decision = Make().Evaluate(Ctx("code.exec", ToolCapabilityClass.Exec, ToolSessionRole.Reader));

        Assert.Equal(PermissionOutcome.Deny, decision.Outcome);
        Assert.Equal(nameof(PolicyReason.RoleNotPermitted), decision.Reason);
    }

    [Fact]
    public void An_unregistered_tool_is_denied()
    {
        var decision = Make().Evaluate(Ctx("code.mystery", ToolCapabilityClass.Read));

        Assert.Equal(PermissionOutcome.Deny, decision.Outcome);
        Assert.Equal(nameof(PolicyReason.UnknownTool), decision.Reason);
    }

    [Fact]
    public void A_wrong_capability_class_label_is_denied()
    {
        var decision = Make().Evaluate(Ctx("code.push", ToolCapabilityClass.Read, branch: "agent/x"));

        Assert.Equal(PermissionOutcome.Deny, decision.Outcome);
    }

    [Fact]
    public void A_bad_session_id_or_missing_args_is_denied()
    {
        var adapter = Make();

        Assert.Equal(PermissionOutcome.Deny, adapter.Evaluate(Ctx("code.read", ToolCapabilityClass.Read, session: "s1")).Outcome);
        Assert.Equal(
            PermissionOutcome.Deny,
            adapter.Evaluate(new ToolCallContext("agent-1", _session, "code.read", ToolCapabilityClass.Read, _hash, "agent", CodingPermissions.SessionTaint.None, Role: ToolSessionRole.Actor)).Outcome);
    }

    [Fact]
    public void Hard_taint_raises_a_notice_tool_to_require_approval()
    {
        var decision = Make().Evaluate(Ctx("code.exec", ToolCapabilityClass.Exec, taint: CodingPermissions.SessionTaint.Hard));

        Assert.Equal(PermissionOutcome.RequireApproval, decision.Outcome);
        Assert.Equal(nameof(PolicyReason.HardTaintTier2), decision.Reason);
    }

    [Fact]
    public void A_tier2_tool_asks_for_approval_and_a_matching_grant_allows_it_as_approved_by_grant()
    {
        var adapter = Make();
        var args = Args("""{"remote":"origin"}""");

        var asked = adapter.Evaluate(Ctx("code.push", ToolCapabilityClass.Push, branch: "agent/x", args: args));
        Assert.Equal(PermissionOutcome.RequireApproval, asked.Outcome);

        var hash = DispatchHash.Compute("code.push", "agent/x", args, []);
        var grant = new ToolApprovalGrant(Guid.NewGuid(), hash, DateTimeOffset.UtcNow.AddHours(1));
        var granted = adapter.Evaluate(Ctx("code.push", ToolCapabilityClass.Push, branch: "agent/x", args: args, grant: grant));

        Assert.Equal(PermissionOutcome.Allow, granted.Outcome);
        Assert.True(granted.ApprovedByGrant);

        var other = adapter.Evaluate(Ctx("code.push", ToolCapabilityClass.Push, branch: "agent/y", args: args, grant: grant));
        Assert.Equal(PermissionOutcome.RequireApproval, other.Outcome);
        Assert.False(other.ApprovedByGrant);
    }

    [Fact]
    public void A_full_quota_window_is_reported_as_rate_limited_denial()
    {
        var adapter = Make();

        Assert.Equal(PermissionOutcome.Allow, adapter.Evaluate(Ctx("code.limited", ToolCapabilityClass.Read)).Outcome);
        var second = adapter.Evaluate(Ctx("code.limited", ToolCapabilityClass.Read));

        Assert.Equal(PermissionOutcome.Deny, second.Outcome);
        Assert.True(second.RateLimited);
    }

    [Fact]
    public void Session_role_names_match_servers_single_roles()
    {
        Assert.Equal(
            Enum.GetNames<SessionRoles>().Where(n => n != nameof(SessionRoles.None)),
            Enum.GetNames<ToolSessionRole>());
    }
}
