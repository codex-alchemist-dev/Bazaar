using Bazaar.Server.Identity;
using Bazaar.Server.Identity.Totp;
using Bazaar.Server.Security.Policy;

namespace Bazaar.Server.Tests.Identity.Totp;

public sealed class TotpAccessPolicyTests
{
    [Fact]
    public void Default_options_map_sensitive_to_human_and_ordinary_to_owner_approval()
    {
        var policy = TotpAccessPolicy.From(new IdentityOptions(), ["bank.example"]);

        Assert.Equal(ToolTier.HumanPerforms, policy.TierFor("bank.example"));
        Assert.Equal(ToolTier.OwnerApproves, policy.TierFor("forum.example"));
    }

    [Fact]
    public void Sensitive_matching_ignores_case()
    {
        var policy = new TotpAccessPolicy(ToolTier.OwnerApproves, ToolTier.HumanPerforms, ["Bank.Example"]);

        Assert.True(policy.IsSensitive("bank.example"));
        Assert.Equal(ToolTier.HumanPerforms, policy.TierFor("BANK.EXAMPLE"));
    }

    [Fact]
    public void Empty_sensitive_list_uses_the_ordinary_tier_everywhere()
    {
        var policy = new TotpAccessPolicy(ToolTier.OwnerApproves, ToolTier.HumanPerforms, []);

        Assert.Equal(ToolTier.OwnerApproves, policy.TierFor("anything"));
    }

    [Fact]
    public void Accepts_the_floor_itself()
    {
        var policy = new TotpAccessPolicy(IdentityOptions.TotpFloor, IdentityOptions.TotpFloor, []);

        Assert.Equal(ToolTier.OwnerApproves, policy.OrdinaryTier);
        Assert.Equal(ToolTier.OwnerApproves, policy.SensitiveTier);
    }

    [Theory]
    [InlineData(ToolTier.Autonomous)]
    [InlineData(ToolTier.AutonomousWithNotice)]
    public void Rejects_ordinary_tier_below_the_floor(ToolTier tier)
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() =>
            new TotpAccessPolicy(tier, ToolTier.HumanPerforms, []));

        Assert.Equal("ordinaryTier", ex.ParamName);
    }

    [Theory]
    [InlineData(ToolTier.Autonomous)]
    [InlineData(ToolTier.AutonomousWithNotice)]
    public void Rejects_sensitive_tier_below_the_floor(ToolTier tier)
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() =>
            new TotpAccessPolicy(ToolTier.OwnerApproves, tier, []));

        Assert.Equal("sensitiveTier", ex.ParamName);
    }

    [Fact]
    public void From_rejects_options_below_the_floor()
    {
        var options = new IdentityOptions { TotpTierOrdinary = ToolTier.Autonomous };

        Assert.Throws<ArgumentOutOfRangeException>(() => TotpAccessPolicy.From(options, []));
    }

    [Fact]
    public void Rejects_undefined_tier_values()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new TotpAccessPolicy((ToolTier)42, ToolTier.HumanPerforms, []));
    }

    [Fact]
    public void Rejects_blank_sensitive_service_ids()
    {
        Assert.Throws<ArgumentException>(() =>
            new TotpAccessPolicy(ToolTier.OwnerApproves, ToolTier.HumanPerforms, ["  "]));
    }

    [Fact]
    public void Rejects_blank_service_query()
    {
        var policy = new TotpAccessPolicy(ToolTier.OwnerApproves, ToolTier.HumanPerforms, []);

        Assert.Throws<ArgumentException>(() => policy.TierFor(""));
    }
}
