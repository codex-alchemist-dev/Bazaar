using System.Text.Json;
using Bazaar.Server.Security.Policy;
using Bazaar.Server.Security.Taint;
using Bazaar.Server.Settings;
using Bazaar.Server.Tests.Security.Policy;

namespace Bazaar.Server.Tests.Settings;

/// <summary>The doc 15 floors as pure checks. Each floor is refused when loosened and allowed when tightened or unchanged.</summary>
public sealed class SecurityFloorsTests
{
    private static JsonElement Json(string text) => JsonSerializer.Deserialize<JsonElement>(text);

    [Theory]
    [InlineData("""["captcha","sms","id","recovery_codes","legal_terms"]""")]
    [InlineData("""["captcha","sms","id","passkeys","legal_terms"]""")]
    [InlineData("""["captcha"]""")]
    [InlineData("""["captcha","sms","passkeys","recovery_codes","legal_terms"]""")]
    [InlineData("""["captcha","sms","id","passkeys","recovery_codes"]""")]
    [InlineData("""["sms","id","passkeys","recovery_codes","legal_terms"]""")]
    [InlineData("[]")]
    public void Tier3_list_refuses_dropping_a_human_only_action(string proposed)
    {
        var check = SecurityFloors.Check("security.tier3.actions", Json(proposed));

        Assert.False(check.Allowed);
        Assert.Contains("floor", check.Reason, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""["captcha","sms","id","passkeys","recovery_codes","legal_terms"]""")]
    [InlineData("""["legal_terms","recovery_codes","passkeys","captcha","sms","id"]""")]
    public void Tier3_list_allows_keeping_or_adding_actions(string proposed)
    {
        Assert.True(SecurityFloors.Check("security.tier3.actions", Json(proposed)).Allowed);
    }

    [Theory]
    [InlineData("481")]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("\"60\"")]
    public void Task_scope_refuses_values_above_the_maximum_or_not_a_positive_whole_number(string proposed)
    {
        Assert.False(SecurityFloors.Check("security.approval.task_scope_minutes", Json(proposed)).Allowed);
    }

    [Theory]
    [InlineData("1")]
    [InlineData("30")]
    [InlineData("60")]
    [InlineData("480")]
    public void Task_scope_allows_values_within_the_maximum(string proposed)
    {
        Assert.True(SecurityFloors.Check("security.approval.task_scope_minutes", Json(proposed)).Allowed);
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("4")]
    [InlineData("\"2\"")]
    public void Own_branch_tier_refuses_values_that_are_not_a_known_tier(string proposed)
    {
        Assert.False(SecurityFloors.Check("security.push.own_branch_untainted_tier", Json(proposed)).Allowed);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("2")]
    [InlineData("3")]
    public void Own_branch_tier_allows_known_tiers(string proposed)
    {
        Assert.True(SecurityFloors.Check("security.push.own_branch_untainted_tier", Json(proposed)).Allowed);
    }

    [Theory]
    [InlineData("\"always\"")]
    [InlineData("\"hard_taint_only\"")]
    public void Tier2_when_untainted_allows_both_values(string proposed)
    {
        Assert.True(SecurityFloors.Check("security.approval.tier2_when_untainted", Json(proposed)).Allowed);
    }

    [Theory]
    [InlineData("\"never\"")]
    [InlineData("\"hard-taint-only\"")]
    [InlineData("1")]
    public void Tier2_when_untainted_refuses_anything_else(string proposed)
    {
        Assert.False(SecurityFloors.Check("security.approval.tier2_when_untainted", Json(proposed)).Allowed);
    }

    [Theory]
    [InlineData("always", Tier2WhenUntainted.Always)]
    [InlineData("hard_taint_only", Tier2WhenUntainted.HardTaintOnly)]
    public void Hard_taint_gate_survives_either_tier2_when_untainted_value(string wire, Tier2WhenUntainted value)
    {
        Assert.True(SecurityFloors.Check("security.approval.tier2_when_untainted", Json($"\"{wire}\"")).Allowed);

        var engine = new ToolPolicyEngine(PolicyFixtures.Standard(), new InMemoryRateBucketStore(), new PolicyOptions(value));
        var decision = engine.Evaluate(PolicyFixtures.Context("repo.push", SessionRoles.Actor, PolicyFixtures.Hard(), args: "{}"));

        Assert.Equal(PolicyOutcome.RequireApproval, decision.Outcome);
        Assert.Equal(PolicyReason.HardTaintTier2, decision.Reason);
    }

    [Theory]
    [InlineData("security.mount.credentials")]
    [InlineData("security.passport.mount_secrets")]
    public void No_setting_can_enable_mounting_credentials(string key)
    {
        var check = SecurityFloors.Check(key, Json("true"));

        Assert.False(check.Allowed);
        Assert.Contains("never mountable", check.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Unknown_security_key_is_refused()
    {
        var check = SecurityFloors.Check("security.approval.bypass", Json("true"));

        Assert.False(check.Allowed);
        Assert.Contains("doc 15 settings table", check.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_shipped_default_passes_its_floors()
    {
        foreach (var definition in SecuritySettings.All)
        {
            var check = SecurityFloors.Check(definition.Name, Json(definition.DefaultValueJson));
            Assert.True(check.Allowed, $"{definition.Name}: {check.Reason}");
        }
    }

    [Fact]
    public void Shipped_security_keys_match_the_doc_15_table()
    {
        string[] expected =
        [
            "security.quota.public_items_per_platform_day", "security.quota.public_issue_pr_per_repo_day",
            "security.quota.new_repos_per_week", "security.quota.mail_sent_per_day", "security.quota.github_writes_per_minute",
            "security.approval.request_cap_per_agent_hour", "security.approval.expiry_hours", "security.approval.task_scope_minutes",
            "security.approval.tier2_when_untainted", "security.tier3.actions", "security.push.own_branch_untainted_tier",
            "security.egress.default_allowlist", "security.backup.include_cookie_profiles", "security.audit.export_target",
            "security.audit.export_interval_hours", "security.scam_mail.protocol_enabled",
        ];

        var names = SecuritySettings.All.Select(d => d.Name).OrderBy(n => n, StringComparer.Ordinal);
        Assert.Equal(expected.OrderBy(n => n, StringComparer.Ordinal), names);
        Assert.All(SecuritySettings.All, d => Assert.Equal(ApprovalTier.Tier2, d.ApprovalTier));
    }
}
