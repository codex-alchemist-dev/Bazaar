using Bazaar.Server.Skimmer.Rules;

namespace Bazaar.Server.Tests.Skimmer;

/// <summary>The rules stage is a pure function: precedence, case-insensitive mentions and the DM setting (doc 13, WP2).</summary>
public sealed class RulesStageTests
{
    private static readonly Guid _gcId = Guid.Parse("0196a1f0-0000-7000-8000-000000000001");
    private static readonly Guid _agentId = Guid.Parse("0196a1f0-0000-7000-8000-000000000002");

    private static GcMessageFacts Facts(
        IReadOnlyList<string>? mentions = null,
        IReadOnlySet<string>? handles = null,
        bool dm = false,
        bool owner = false,
        bool critical = false,
        bool quarantined = false) =>
        new(_gcId, 1, _agentId, handles ?? new HashSet<string>(["Charles"]), mentions ?? [], dm, owner, critical, quarantined);

    [Fact]
    public void Quarantine_beats_mention_and_defers()
    {
        var result = RulesStage.Evaluate(Facts(mentions: ["charles"], quarantined: true), new GateRuleSettings());

        Assert.Equal(new RuleResult("quarantine", "defer", true), result);
    }

    [Fact]
    public void Mention_beats_dm_and_critical()
    {
        var result = RulesStage.Evaluate(Facts(mentions: ["Charles"], dm: true, critical: true, owner: true), new GateRuleSettings());

        Assert.Equal(new RuleResult("mention", "wake", true), result);
    }

    [Fact]
    public void Dm_with_DmAlwaysWakes_false_falls_through_to_undecided()
    {
        var result = RulesStage.Evaluate(Facts(dm: true), new GateRuleSettings(DmAlwaysWakes: false));

        Assert.Equal(RuleResult.Undecided, result);
        Assert.False(result.Decided);
    }

    [Fact]
    public void Dm_with_DmAlwaysWakes_true_wakes_as_dm()
    {
        var result = RulesStage.Evaluate(Facts(dm: true), new GateRuleSettings());

        Assert.Equal(new RuleResult("dm", "wake", true), result);
    }

    [Fact]
    public void Critical_message_wakes_as_critical()
    {
        var result = RulesStage.Evaluate(Facts(critical: true), new GateRuleSettings());

        Assert.Equal(new RuleResult("critical", "wake", true), result);
    }

    [Fact]
    public void Owner_author_wakes_as_critical()
    {
        var result = RulesStage.Evaluate(Facts(owner: true), new GateRuleSettings());

        Assert.Equal(new RuleResult("critical", "wake", true), result);
    }

    [Fact]
    public void Ordinary_message_is_undecided()
    {
        var result = RulesStage.Evaluate(Facts(mentions: ["someone-else"]), new GateRuleSettings());

        Assert.Equal(RuleResult.Undecided, result);
    }

    [Fact]
    public void Mention_matching_ignores_case_in_both_directions()
    {
        var handles = new HashSet<string>(["CHARLES-Bot"]);

        var result = RulesStage.Evaluate(Facts(mentions: ["charles-bot"], handles: handles), new GateRuleSettings());

        Assert.Equal(new RuleResult("mention", "wake", true), result);
    }

    [Fact]
    public void Empty_agent_handles_are_undecided_even_with_mentions()
    {
        var result = RulesStage.Evaluate(Facts(mentions: ["charles"], handles: new HashSet<string>()), new GateRuleSettings());

        Assert.Equal(RuleResult.Undecided, result);
    }

    [Fact]
    public void Null_facts_throw()
    {
        Assert.Throws<ArgumentNullException>(() => RulesStage.Evaluate(null!, new GateRuleSettings()));
    }

    [Fact]
    public void Null_mentioned_handles_throw()
    {
        var facts = Facts() with { MentionedHandles = null! };

        Assert.Throws<ArgumentNullException>(() => RulesStage.Evaluate(facts, new GateRuleSettings()));
    }

    [Fact]
    public void Null_agent_handles_throw()
    {
        var facts = Facts() with { AgentHandles = null! };

        Assert.Throws<ArgumentNullException>(() => RulesStage.Evaluate(facts, new GateRuleSettings()));
    }
}
