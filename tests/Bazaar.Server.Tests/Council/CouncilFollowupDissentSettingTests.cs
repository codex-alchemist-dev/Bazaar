using System.Text.Json;
using Bazaar.Server.Council.Service;
using Bazaar.Server.Knowledge;
using Bazaar.Server.Settings;

namespace Bazaar.Server.Tests.Council;

/// <summary>council.followup_dissent_share: registration, default and validation (docs/design/10-council-and-voting.md, Settings).</summary>
public class CouncilFollowupDissentSettingTests
{
    private static JsonElement Json(string raw) => JsonElement.Parse(raw);

    [Fact]
    public void The_key_is_registered_with_the_doc_default()
    {
        Assert.True(SettingRegistry.Known.TryGet("council.followup_dissent_share", out var definition));
        Assert.Same(KnowledgeSettings.CouncilFollowupDissentShareKey, definition);
        Assert.Equal(0.33m, KnowledgeSettings.CouncilFollowupDissentShareKey.Default);
        Assert.Equal(SettingScope.Install, definition!.Scope);
        Assert.Equal(SettingRole.Operator, definition.WhoMayChange);
        Assert.Equal(ApprovalTier.None, definition.ApprovalTier);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("0.5")]
    [InlineData("1")]
    public void Values_from_zero_to_one_inclusive_are_accepted(string raw)
    {
        Assert.Null(KnowledgeSettings.CouncilFollowupDissentShareKey.CheckValue(Json(raw)));
    }

    [Theory]
    [InlineData("-0.01")]
    [InlineData("1.01")]
    public void Values_outside_zero_to_one_are_rejected(string raw)
    {
        Assert.NotNull(KnowledgeSettings.CouncilFollowupDissentShareKey.CheckValue(Json(raw)));
    }

    [Fact]
    public void A_non_number_is_rejected()
    {
        Assert.NotNull(KnowledgeSettings.CouncilFollowupDissentShareKey.CheckValue(Json("\"high\"")));
    }

    [Fact]
    public void The_default_serialises_as_a_plain_decimal()
    {
        Assert.Equal("0.33", KnowledgeSettings.CouncilFollowupDissentShareKey.DefaultValueJson);
    }

    [Theory]
    [InlineData(-0.01)]
    [InlineData(1.01)]
    public void CouncilOptions_rejects_a_share_outside_zero_to_one(double share)
    {
        var options = new CouncilOptions { FollowupDissentShare = (decimal)share };

        Assert.Throws<ArgumentException>(options.Validate);
    }

    [Fact]
    public void CouncilOptions_default_share_matches_the_setting_default()
    {
        Assert.Equal(KnowledgeSettings.CouncilFollowupDissentShareKey.Default, new CouncilOptions().FollowupDissentShare);
        new CouncilOptions().Validate();
    }
}
