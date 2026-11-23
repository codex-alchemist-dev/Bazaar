using System.Text.Json;
using Bazaar.Server.Council.Service;
using Bazaar.Server.Council.Settings;
using Bazaar.Server.Settings;

namespace Bazaar.Server.Tests.Council;

/// <summary>The council.* keys of doc 10 (Settings table): registration, defaults, bounds and approval tiers.</summary>
public class CouncilSettingsRegistryTests
{
    private static JsonElement Json(string raw) => JsonElement.Parse(raw);

    private static SettingRegistry Combined() =>
        BuiltInSettingRegistries.WithContributions([new CouncilSettingContribution()]);

    [Fact]
    public void Every_council_key_of_the_doc_is_in_the_combined_registry_once()
    {
        var registry = Combined();

        Assert.Equal(23, CouncilSettings.Names.Count);
        Assert.Equal(CouncilSettings.Names.Count, CouncilSettings.Names.Distinct().Count());
        foreach (var name in CouncilSettings.Names)
        {
            Assert.True(registry.TryGet(name, out var definition), name);
            Assert.Equal(SettingScope.Install, definition!.Scope);
            Assert.Equal(SettingRole.Operator, definition.WhoMayChange);
        }
    }

    [Fact]
    public void The_module_does_not_redeclare_keys_registered_elsewhere()
    {
        Assert.DoesNotContain(CouncilSettings.All, k => CouncilSettings.RegisteredElsewhere.Contains(k.Name));
    }

    [Fact]
    public void Defaults_match_CouncilOptions_and_the_doc()
    {
        var options = new CouncilOptions();
        Assert.Equal(options.QuorumMin, CouncilSettings.QuorumMin.Default);
        Assert.Equal(options.VoteExtensionsMax, CouncilSettings.VoteExtensionsMax.Default);
        Assert.Equal(options.VoteDeadlineCapHours, CouncilSettings.VoteDeadlineCapHours.Default);
        Assert.Equal(options.OpenVotesPerAgentDay, CouncilSettings.OpenVotesPerAgentDay.Default);
        Assert.Equal(options.ReopenCooldownHours, CouncilSettings.ReopenCooldownHours.Default);
        Assert.Equal(options.UnanimityElectorate, CouncilSettings.UnanimityElectorateKey.Default);
        Assert.Equal(options.ExcludeAuthor, CouncilSettings.ExcludeAuthor.Default);
        Assert.Equal(options.ExcludeMembershipSubject, CouncilSettings.ExcludeMembershipSubject.Default);
        Assert.Equal(options.OwnerVotes, CouncilSettings.OwnerVotes.Default);
        Assert.Equal(options.FollowupEnabled, CouncilSettings.FollowupEnabled.Default);
        Assert.Equal(options.ReasonMinSentences, CouncilSettings.ReasonMinSentences.Default);
        Assert.Equal(options.ReasonMaxChars, CouncilSettings.ReasonMaxChars.Default);
        Assert.Equal(options.ReasonOrder, CouncilSettings.ReasonOrderKey.Default);
        Assert.Equal(1.0m, CouncilSettings.ReputationWeight.Default);
        Assert.True(CouncilSettings.AbstainCountsQuorum.Default);
        Assert.Equal(1, CouncilSettings.VoteDeadlineWorkingDays.Default);
        Assert.Equal(4, CouncilSettings.EscalationRenotifyHours.Default);
        var pilot = new Bazaar.Server.Council.Pilot.PilotSettings();
        Assert.Equal(pilot.HoldoutShare, CouncilSettings.PilotHoldoutShare.Default);
        Assert.Equal(pilot.StopFlipShare, CouncilSettings.PilotStopFlipShare.Default);
        Assert.Equal(pilot.TargetVotes, CouncilSettings.PilotTargetVotes.Default);
    }

    [Theory]
    [InlineData("1.0", true)]
    [InlineData("1.5", true)]
    [InlineData("0.99", false)]
    [InlineData("1.51", false)]
    public void Reputation_weight_is_bounded_from_1_to_1_5(string raw, bool valid) =>
        Assert.Equal(valid, CouncilSettings.ReputationWeight.CheckValue(Json(raw)) is null);

    [Theory]
    [InlineData("quorum_min", "0")]
    [InlineData("vote_deadline_cap_hours", "0")]
    [InlineData("vote_extensions_max", "-1")]
    [InlineData("reason_max_chars", "0")]
    [InlineData("reason_min_sentences", "-1")]
    [InlineData("open_votes_per_agent_day", "0")]
    [InlineData("reopen_cooldown_hours", "-1")]
    [InlineData("pilot_target_votes", "0")]
    [InlineData("pilot_holdout_share", "1.1")]
    [InlineData("pilot_stop_flip_share", "-0.1")]
    [InlineData("vote_types_enabled", "[]")]
    [InlineData("reason_order", "\"shuffled\"")]
    [InlineData("unanimity_electorate", "\"everyone\"")]
    public void Values_below_a_floor_or_unknown_are_rejected(string name, string raw)
    {
        Assert.True(Combined().TryGet("council." + name, out var definition));
        Assert.NotNull(definition!.CheckValue(Json(raw)));
    }

    [Fact]
    public void Wire_names_of_enums_are_snake_case()
    {
        Assert.Equal("\"eligible_after_exclusions\"", CouncilSettings.UnanimityElectorateKey.DefaultValueJson);
        Assert.Equal("\"random\"", CouncilSettings.ReasonOrderKey.DefaultValueJson);
        Assert.Equal("[\"approve_reject\",\"option_choice\"]", CouncilSettings.VoteTypesEnabled.DefaultValueJson);
        Assert.Null(CouncilSettings.UnanimityElectorateKey.CheckValue(Json("\"all_gc_members\"")));
    }

    [Fact]
    public void Keys_the_doc_marks_as_needing_approval_are_tier_2_and_the_rest_apply_at_once()
    {
        var approval = new[]
        {
            CouncilSettings.ReputationWeight.Name, CouncilSettings.OwnerVotes.Name, CouncilSettings.ExcludeAuthor.Name,
            CouncilSettings.ExcludeMembershipSubject.Name, CouncilSettings.UnanimityElectorateKey.Name, CouncilSettings.FollowupEnabled.Name,
        };
        foreach (var key in CouncilSettings.All)
        {
            Assert.Equal(approval.Contains(key.Name) ? ApprovalTier.Tier2 : ApprovalTier.None, key.ApprovalTier);
        }
    }

    [Fact]
    public void A_non_operator_cannot_change_a_key()
    {
        var evaluation = CouncilSettings.QuorumMin.Evaluate(new SettingActor("agent", SettingRole.None), Json("4"));
        Assert.Equal(SettingWriteOutcome.Rejected, evaluation.Outcome);
    }
}
