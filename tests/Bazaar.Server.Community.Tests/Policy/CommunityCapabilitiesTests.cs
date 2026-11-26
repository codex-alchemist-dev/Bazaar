using System.Text.Json;
using Bazaar.Server.Community.Policy;
using Bazaar.Server.Security.Policy;
using Bazaar.Server.Security.Taint;

namespace Bazaar.Server.Community.Tests.Policy;

public class CommunityCapabilitiesTests
{
    private static readonly DateTimeOffset _now = new(2026, 10, 30, 12, 0, 0, TimeSpan.Zero);

    private static readonly Guid _session = Guid.Parse("0196a6f0-0000-7000-8000-000000000401");

    private static readonly Guid _sourceId = Guid.Parse("0196a6f0-0000-7000-8000-000000000402");

    private static readonly string[] _platforms = ["github", "gitlab"];

    private static CapabilityRegistry Registry(Func<CommunityAction, RateRule?>? rateFor = null) =>
        CapabilityRegistry.Create(CommunityCapabilities.Create(_platforms, rateFor));

    private static PolicyDecision Decide(
        CapabilityRegistry registry,
        string tool,
        SessionRoles role,
        SessionTaint? taint = null)
    {
        var engine = new ToolPolicyEngine(registry, new InMemoryRateBucketStore(), PolicyOptions.Default);
        var context = new DispatchContext(
            "agent-a",
            _session,
            role,
            tool,
            "github",
            "codex-alchemist-dev/Bazaar",
            JsonElement.Parse("""{"title":"Crash on empty input"}"""),
            taint ?? SessionTaint.Clean,
            _now);
        return engine.Evaluate(context);
    }

    /// <summary>A session that has seen external content, so it is hard-tainted.</summary>
    private static SessionTaint HardTainted() =>
        TaintRules.Apply(SessionTaint.Clean, new TaintInput(_sourceId, ContentTrust.External), TaintPolicy.Default);

    [Fact]
    public void Read_is_autonomous_and_allowed_in_a_clean_actor_session()
    {
        var registry = Registry();
        var tool = CommunityTierMap.ToolName(CommunityAction.Read, "github");

        var decision = Decide(registry, tool, SessionRoles.Actor);

        Assert.True(registry.TryGet(tool, out var capability));
        Assert.Equal(ToolTier.Autonomous, capability.Tier);
        Assert.Equal(new PolicyDecision(PolicyOutcome.Allow, PolicyReason.Autonomous), decision);
    }

    [Fact]
    public void New_issue_in_a_clean_actor_session_requires_approval_not_allow()
    {
        var registry = Registry();
        var tool = CommunityTierMap.ToolName(CommunityAction.NewIssue, "github");

        var decision = Decide(registry, tool, SessionRoles.Actor, SessionTaint.Clean);

        Assert.Equal(PolicyOutcome.RequireApproval, decision.Outcome);
        Assert.Equal(PolicyReason.OwnerApprovalRequired, decision.Reason);
    }

    [Fact]
    public void Hard_tainted_actor_posting_a_new_issue_without_a_grant_requires_approval_not_deny()
    {
        // The engine denies only Denied and Tier 3 tools. A Tier 2 write under hard taint is held for approval,
        // so the owner can still approve the one call. Doc 15: hard taint blocks Tier 2 without a per-action approval.
        var registry = Registry();
        var tool = CommunityTierMap.ToolName(CommunityAction.NewIssue, "github");

        var decision = Decide(registry, tool, SessionRoles.Actor, HardTainted());

        Assert.Equal(PolicyOutcome.RequireApproval, decision.Outcome);
        Assert.Equal(PolicyReason.HardTaintTier2, decision.Reason);
    }

    [Fact]
    public void Reader_session_cannot_call_any_write_tool()
    {
        var registry = Registry();
        var writes = Enum.GetValues<CommunityAction>().Where(a => a != CommunityAction.Read);

        foreach (var action in writes)
        {
            foreach (var platform in _platforms)
            {
                var tool = CommunityTierMap.ToolName(action, platform);

                var decision = Decide(registry, tool, SessionRoles.Reader, HardTainted());

                Assert.True(decision.Outcome == PolicyOutcome.Deny && decision.Reason == PolicyReason.RoleNotPermitted,
                    $"{tool} from a reader session returned {decision.Outcome}/{decision.Reason}.");
            }
        }
    }

    [Fact]
    public void Tool_names_are_unique_across_platforms_and_actions()
    {
        var tools = CommunityCapabilities.Create(["github", "gitlab", "codeberg"]);

        var names = tools.Select(t => t.Name).ToList();

        Assert.Equal(3 * Enum.GetValues<CommunityAction>().Length, names.Count);
        Assert.Equal(names.Count, names.Distinct(StringComparer.Ordinal).Count());

        // The registry accepts the full set, so no name clashes.
        _ = CapabilityRegistry.Create(tools);
    }

    [Fact]
    public void Repeated_platform_keys_produce_repeated_names_that_the_registry_rejects()
    {
        var tools = CommunityCapabilities.Create(["github", "GitHub"]);

        Assert.Throws<ArgumentException>(() => CapabilityRegistry.Create(tools));
    }

    [Theory]
    [InlineData("git hub")]
    [InlineData("git_hub")]
    [InlineData("git.hub")]
    [InlineData("git/hub")]
    [InlineData("gité")]
    [InlineData("")]
    public void ToolName_rejects_platforms_with_spaces_or_other_invalid_characters(string platform)
    {
        Assert.Throws<ArgumentException>(() => CommunityTierMap.ToolName(CommunityAction.NewIssue, platform));
    }

    [Fact]
    public void ToolName_lower_cases_the_platform_and_snake_cases_the_action()
    {
        Assert.Equal("community.github.new_issue", CommunityTierMap.ToolName(CommunityAction.NewIssue, "GitHub"));
        Assert.Equal("community.git-hub.reply_approved_thread", CommunityTierMap.ToolName(CommunityAction.ReplyApprovedThread, "Git-Hub"));
        Assert.Equal("community.x2.outreach_email", CommunityTierMap.ToolName(CommunityAction.OutreachEmail, "x2"));
    }

    [Fact]
    public void Rate_rule_from_rateFor_is_attached_to_its_capability()
    {
        var rule = CommunityQuotas.PublicIssuePrPerRepoDay;

        var registry = Registry(action => action == CommunityAction.NewIssue ? rule : null);

        Assert.True(registry.TryGet(CommunityTierMap.ToolName(CommunityAction.NewIssue, "github"), out var issue));
        Assert.Same(rule, issue.Rate);

        Assert.True(registry.TryGet(CommunityTierMap.ToolName(CommunityAction.NewPost, "github"), out var post));
        Assert.Null(post.Rate);
    }
}
