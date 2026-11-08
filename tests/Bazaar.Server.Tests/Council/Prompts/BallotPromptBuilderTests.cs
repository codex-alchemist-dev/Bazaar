using Bazaar.Server.Council;
using Bazaar.Server.Council.Prompts;

namespace Bazaar.Server.Tests.Council.Prompts;

public class BallotPromptBuilderTests
{
    private const string _voter = "agent-1";

    private static readonly string[] _peers = ["agent-2", "agent-3", "agent-4"];

    private static readonly CouncilMotion _approveMotion = new(
        "Adopt the weekly review rota", VoteKind.ApproveReject, MotionClass.Ordinary, []);

    private static readonly CouncilMotion _choiceMotion = new(
        "Pick the release cadence", VoteKind.OptionChoice, MotionClass.Ordinary, ["weekly", "fortnightly"]);

    private static readonly Ballot _ownBallot = new(_voter, ApprovalChoices.No, "The rota adds review load without a clear gain.", BallotPhase.Independent);

    private static IReadOnlyList<RevealedReason> Revealed() =>
    [
        new RevealedReason("Reason A", "The rota cuts queue time for reviewers."),
        new RevealedReason("Reason B", "Review load is already high and the rota adds to it."),
    ];

    private static BallotPromptRequest Independent(CouncilMotion? motion = null, BallotPromptRules? rules = null, bool tainted = false) =>
        new(motion ?? _approveMotion, _voter, BallotPhase.Independent, PromptContext.Working, tainted, _peers, Rules: rules);

    private static BallotPromptRequest FollowUp(IReadOnlyList<RevealedReason>? revealed = null, PromptContext context = PromptContext.Working) =>
        new(_approveMotion, _voter, BallotPhase.FollowUp, context, false, _peers, _ownBallot, revealed ?? Revealed());

    [Fact]
    public void Independent_prompt_contains_motion_and_voter_instructions_only()
    {
        var prompt = BallotPromptBuilder.Build(Independent());

        Assert.Contains("Adopt the weekly review rota", prompt.Text);
        Assert.Contains("yes", prompt.Text);
        Assert.Contains("no", prompt.Text);
        Assert.Contains("abstain", prompt.Text);
        Assert.Contains("sealed independent ballot", prompt.Text);
        Assert.Equal(BallotPhase.Independent, prompt.Phase);
        Assert.DoesNotContain("Reason A", prompt.Text);
        Assert.DoesNotContain("Your independent ballot", prompt.Text);
        foreach (var peer in _peers)
        {
            Assert.DoesNotContain(peer, prompt.Text);
        }
    }

    [Fact]
    public void Independent_request_cannot_carry_revealed_reasons()
    {
        var request = Independent() with { Revealed = Revealed() };

        Assert.Throws<ArgumentException>(() => BallotPromptBuilder.Build(request));
    }

    [Fact]
    public void Independent_request_cannot_carry_an_own_ballot()
    {
        var request = Independent() with { OwnIndependentBallot = _ownBallot };

        Assert.Throws<ArgumentException>(() => BallotPromptBuilder.Build(request));
    }

    [Fact]
    public void Follow_up_prompt_shows_aliases_and_no_voter_ids()
    {
        var prompt = BallotPromptBuilder.Build(FollowUp());

        Assert.Contains("Reason A: The rota cuts queue time for reviewers.", prompt.Text);
        Assert.Contains("Reason B: Review load is already high and the rota adds to it.", prompt.Text);
        Assert.Contains("Your independent ballot:", prompt.Text);
        Assert.Contains("Choice: no", prompt.Text);
        Assert.Equal(BallotPhase.FollowUp, prompt.Phase);
        Assert.DoesNotContain(_voter, prompt.Text);
        foreach (var peer in _peers)
        {
            Assert.DoesNotContain(peer, prompt.Text);
        }
    }

    [Fact]
    public void Follow_up_prompt_keeps_the_stored_reason_order()
    {
        var prompt = BallotPromptBuilder.Build(FollowUp());

        Assert.True(prompt.Text.IndexOf("Reason A:", StringComparison.Ordinal) < prompt.Text.IndexOf("Reason B:", StringComparison.Ordinal));
    }

    [Fact]
    public void Instructions_state_default_min_sentences_and_max_chars()
    {
        var prompt = BallotPromptBuilder.Build(Independent());

        Assert.Contains("at least 2 sentences", prompt.Text);
        Assert.Contains("at most 2000 characters", prompt.Text);
    }

    [Fact]
    public void Instructions_state_configured_min_sentences_and_max_chars()
    {
        var prompt = BallotPromptBuilder.Build(Independent(rules: new BallotPromptRules(3, 500)));

        Assert.Contains("at least 3 sentences", prompt.Text);
        Assert.Contains("at most 500 characters", prompt.Text);
    }

    [Fact]
    public void Follow_up_instructions_state_the_same_length_rules()
    {
        var prompt = BallotPromptBuilder.Build(FollowUp());

        Assert.Contains("at least 2 sentences", prompt.Text);
        Assert.Contains("at most 2000 characters", prompt.Text);
    }

    [Fact]
    public void Option_choice_prompt_lists_the_options_and_the_bigger_change_rule()
    {
        var motion = _choiceMotion with { MotionClass = MotionClass.BiggerChange };

        var prompt = BallotPromptBuilder.Build(Independent(motion));

        Assert.Contains("- weekly", prompt.Text);
        Assert.Contains("- fortnightly", prompt.Text);
        Assert.DoesNotContain("abstain", prompt.Text);
        Assert.Contains("every eligible voter makes the same choice", prompt.Text);
    }

    [Fact]
    public void Tainted_flag_passes_through_to_the_prompt_record()
    {
        Assert.True(BallotPromptBuilder.Build(Independent(tainted: true)).TaintedAtWrite);
        Assert.False(BallotPromptBuilder.Build(Independent(tainted: false)).TaintedAtWrite);
    }

    [Fact]
    public void Thinking_context_is_recorded_on_the_prompt_after_reveal()
    {
        var prompt = BallotPromptBuilder.Build(FollowUp(context: PromptContext.Thinking));

        Assert.Equal(PromptContext.Thinking, prompt.Context);
    }

    [Fact]
    public void Follow_up_rejects_an_own_ballot_from_another_voter()
    {
        var foreign = _ownBallot with { VoterId = "agent-2" };
        var request = FollowUp() with { OwnIndependentBallot = foreign };

        Assert.Throws<ArgumentException>(() => BallotPromptBuilder.Build(request));
    }

    [Fact]
    public void Follow_up_rejects_a_choice_the_motion_does_not_offer()
    {
        var request = FollowUp() with { OwnIndependentBallot = _ownBallot with { Choice = "weekly" } };

        Assert.Throws<ArgumentException>(() => BallotPromptBuilder.Build(request));
    }

    [Fact]
    public void Follow_up_rejects_duplicate_reason_labels()
    {
        IReadOnlyList<RevealedReason> duplicated =
        [
            new RevealedReason("Reason A", "First."),
            new RevealedReason("Reason A", "Second."),
        ];

        Assert.Throws<ArgumentException>(() => BallotPromptBuilder.Build(FollowUp(duplicated)));
    }

    [Fact]
    public void Guard_denies_a_motion_that_names_a_peer_voter_id()
    {
        var motion = _approveMotion with { Subject = "Promote agent-3 to lead" };

        var exception = Assert.Throws<CouncilPrivacyViolationException>(() => BallotPromptBuilder.Build(Independent(motion)));

        Assert.Contains("peer voter id", exception.Reason);
    }

    [Fact]
    public void Blank_voter_id_is_rejected()
    {
        var request = Independent() with { VoterId = " " };

        Assert.Throws<ArgumentException>(() => BallotPromptBuilder.Build(request));
    }
}
