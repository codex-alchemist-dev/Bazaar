using Bazaar.Contracts.Gc;
using Bazaar.Contracts.Soul;
using Bazaar.Server.Souls.Judgments;

namespace Bazaar.Server.Tests.Souls.Judgments;

/// <summary>The Markdown decision file and its slug (docs/design/07-soul-system.md, "Auditable judgments").</summary>
public sealed class JudgmentDecisionFileTests
{
    [Fact]
    public void Front_matter_carries_the_ids_the_soul_version_and_the_decision_kind()
    {
        var record = JudgmentFixtures.Record();

        var file = JudgmentDecisionFile.Build(record);

        Assert.Equal(record.DecisionId, file.DecisionId);
        Assert.Equal("promote-ada-to-reviewer", file.Slug);
        Assert.Contains("decision_id: \"44444444-4444-4444-4444-444444444444\"\n", file.Markdown);
        Assert.Contains("soul_id: \"11111111-1111-1111-1111-111111111111\"\n", file.Markdown);
        Assert.Contains("soul_version: 7\n", file.Markdown);
        Assert.Contains("gc_id: \"22222222-2222-2222-2222-222222222222\"\n", file.Markdown);
        Assert.Contains("type: decision\n", file.Markdown);
        Assert.Contains("kind: \"promotion\"\n", file.Markdown);
        Assert.Contains("confidence: \"medium\"\n", file.Markdown);
        Assert.Contains("decided_at: \"2026-10-30T09:00:00Z\"\n", file.Markdown);
        Assert.StartsWith("---\n", file.Markdown);
    }

    [Fact]
    public void The_body_lists_options_choice_reasoning_and_each_input_with_its_provenance()
    {
        var file = JudgmentDecisionFile.Build(JudgmentFixtures.Record());

        Assert.Contains("## Options\n\n- promote\n- hold\n", file.Markdown);
        Assert.Contains("## Chosen\n\npromote\n", file.Markdown);
        Assert.Contains("## Reasoning\n\nAda reviewed eight PRs with no reverts", file.Markdown);
        Assert.Contains("- seq 1042, source_trust external\n", file.Markdown);
        Assert.Contains("- seq 1050, source_trust owner\n", file.Markdown);
    }

    [Fact]
    public void A_judgment_with_no_inputs_says_so()
    {
        var file = JudgmentDecisionFile.Build(JudgmentFixtures.Record() with { Inputs = [] });

        Assert.Contains("## Inputs\n\nNone recorded.\n", file.Markdown);
    }

    [Fact]
    public void A_title_with_quotes_stays_inside_one_quoted_front_matter_value()
    {
        var file = JudgmentDecisionFile.Build(JudgmentFixtures.Record(title: "Promote \"Ada\" to: reviewer"));

        Assert.Contains("title: \"Promote \\\"Ada\\\" to: reviewer\"\n", file.Markdown);
        Assert.Contains("# Promote \"Ada\" to: reviewer\n", file.Markdown);
    }

    [Fact]
    public void Every_kind_and_confidence_has_a_stable_name()
    {
        foreach (var kind in Enum.GetValues<JudgmentKind>())
        {
            var file = JudgmentDecisionFile.Build(JudgmentFixtures.Record() with { Kind = kind });
            Assert.Contains($"kind: \"{kind.ToString().ToLowerInvariant()}\"\n", file.Markdown);
        }

        foreach (var confidence in Enum.GetValues<JudgmentConfidence>())
        {
            var file = JudgmentDecisionFile.Build(JudgmentFixtures.Record() with { Confidence = confidence });
            Assert.Contains($"confidence: \"{confidence.ToString().ToLowerInvariant()}\"\n", file.Markdown);
        }
    }

    [Fact]
    public void Every_source_trust_level_has_a_name_in_the_provenance_list()
    {
        var inputs = Enum.GetValues<SourceTrust>().Select((trust, i) => new JudgmentInput(i + 1, trust)).ToList();

        var file = JudgmentDecisionFile.Build(JudgmentFixtures.Record() with { Inputs = inputs });

        Assert.Contains("- seq 1, source_trust owner\n", file.Markdown);
        Assert.Contains("- seq 2, source_trust system\n", file.Markdown);
        Assert.Contains("- seq 3, source_trust member-human\n", file.Markdown);
        Assert.Contains("- seq 4, source_trust agent\n", file.Markdown);
        Assert.Contains("- seq 5, source_trust external\n", file.Markdown);
        Assert.Contains("- seq 6, source_trust quarantined\n", file.Markdown);
    }

    [Theory]
    [InlineData("Promote Ada to Reviewer!", "promote-ada-to-reviewer")]
    [InlineData("  --  ", "decision")]
    [InlineData("", "decision")]
    [InlineData("Café ünïcode", "caf-n-code")]
    [InlineData("Retire: v2 (final)", "retire-v2-final")]
    public void Slugs_are_lowercase_hyphenated_and_never_empty(string title, string expected)
    {
        Assert.Equal(expected, JudgmentDecisionFile.Slug(title));
    }

    [Fact]
    public void Long_titles_are_cut_to_sixty_characters_without_a_trailing_hyphen()
    {
        var title = string.Join(' ', Enumerable.Repeat("abcdefghij", 8));

        var slug = JudgmentDecisionFile.Slug(title);

        Assert.True(slug.Length <= 60, slug);
        Assert.DoesNotContain("--", slug);
        Assert.False(slug.EndsWith('-'));
        Assert.Equal("abcdefghij-abcdefghij-abcdefghij-abcdefghij-abcdefghij-abcde", slug);
    }
}
