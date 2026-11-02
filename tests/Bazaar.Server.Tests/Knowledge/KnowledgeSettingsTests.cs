using Bazaar.Server.Knowledge;
using Bazaar.Server.Settings;

namespace Bazaar.Server.Tests.Knowledge;

public sealed class KnowledgeSettingsTests
{
    [Fact]
    public void Known_registry_holds_every_doc_25_key()
    {
        var names = SettingRegistry.Known.Definitions.Select(d => d.Name).ToHashSet(StringComparer.Ordinal);

        Assert.Equal(KnowledgeSettings.All.Count, KnowledgeSettings.All.Count(d => names.Contains(d.Name)));
        Assert.Contains("hive.grant_review_days", names);
        Assert.Contains("analytics.agent_metrics_visibility", names);
        Assert.Contains("analytics.morale_signals", names);
        Assert.Contains("health.coverage_drop_points", names);
        Assert.Contains("health.build_failure_streak", names);
        Assert.Contains("archive.chapter_cadence", names);
        Assert.Contains("archive.include_human_text", names);
        Assert.Contains("archive.include_council_reasons", names);
        Assert.Contains("council.bigger_change_classes", names);
        Assert.Contains("gmt.lesson_reviewer", names);
        Assert.Contains("metrics.raw_retention_days", names);
    }

    [Fact]
    public void Defaults_match_the_doc_25_table()
    {
        Assert.Equal("30", KnowledgeSettings.HiveGrantReviewDays.DefaultValueJson);
        Assert.Equal("\"owner_plus_self\"", KnowledgeSettings.AgentMetricsVisibilityKey.DefaultValueJson);
        Assert.Equal("2", KnowledgeSettings.HealthCoverageDropPoints.DefaultValueJson);
        Assert.Equal("3", KnowledgeSettings.HealthBuildFailureStreak.DefaultValueJson);
        Assert.Equal("[\"release_tag\",\"monthly\"]", KnowledgeSettings.ArchiveChapterCadenceKey.DefaultValueJson);
        Assert.Equal("false", KnowledgeSettings.ArchiveIncludeHumanText.DefaultValueJson);
        Assert.Equal("true", KnowledgeSettings.ArchiveIncludeCouncilReasons.DefaultValueJson);
        Assert.Equal("[\"new_agent\",\"tool_grant\"]", KnowledgeSettings.CouncilBiggerChangeClassesKey.DefaultValueJson);
        Assert.Equal("\"architect\"", KnowledgeSettings.GmtLessonReviewerKey.DefaultValueJson);
        Assert.Equal("90", KnowledgeSettings.MetricsRawRetentionDays.DefaultValueJson);
    }

    [Fact]
    public void Widening_metrics_visibility_to_peers_needs_approval_but_owner_only_does_not()
    {
        var actor = new SettingActor("test-operator", SettingRole.Operator);

        var peers = KnowledgeSettings.AgentMetricsVisibilityKey.Evaluate(actor, Json("\"peers\""));
        var owner = KnowledgeSettings.AgentMetricsVisibilityKey.Evaluate(actor, Json("\"owner\""));

        Assert.Equal(SettingWriteOutcome.PendingApproval, peers.Outcome);
        Assert.Equal(SettingWriteOutcome.Accepted, owner.Outcome);
    }

    [Fact]
    public void Hive_review_window_rejects_values_outside_0_to_365()
    {
        Assert.NotNull(KnowledgeSettings.HiveGrantReviewDays.CheckValue(Json("366")));
        Assert.Null(KnowledgeSettings.HiveGrantReviewDays.CheckValue(Json("0")));
    }

    [Fact]
    public void Human_text_archive_opt_in_is_held_for_approval()
    {
        var evaluation = KnowledgeSettings.ArchiveIncludeHumanText.Evaluate(new SettingActor("test-owner", SettingRole.Owner), Json("true"));

        Assert.Equal(SettingWriteOutcome.PendingApproval, evaluation.Outcome);
    }

    [Fact]
    public void Host_registry_is_unchanged_by_the_knowledge_keys()
    {
        Assert.DoesNotContain(SettingRegistry.Host.Definitions, d => d.Name.StartsWith("archive.", StringComparison.Ordinal));
    }

    private static System.Text.Json.JsonElement Json(string text) => System.Text.Json.JsonElement.Parse(text);
}
