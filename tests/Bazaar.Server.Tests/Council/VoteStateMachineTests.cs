using Bazaar.Server.Council;

namespace Bazaar.Server.Tests.Council;

public class VoteStateMachineTests
{
    private static readonly Dictionary<(VoteState, VoteEvent), VoteState> _legal = new()
    {
        [(VoteState.Collecting, VoteEvent.AllEligibleCast)] = VoteState.Revealed,
        [(VoteState.Collecting, VoteEvent.DeadlineWithQuorum)] = VoteState.Revealed,
        [(VoteState.Collecting, VoteEvent.DeadlineExtended)] = VoteState.Collecting,
        [(VoteState.Collecting, VoteEvent.DeadlineWithoutQuorum)] = VoteState.Escalated,
        [(VoteState.Collecting, VoteEvent.Withdraw)] = VoteState.Withdrawn,
        [(VoteState.Revealed, VoteEvent.Decide)] = VoteState.Decided,
        [(VoteState.Revealed, VoteEvent.RequireFollowUp)] = VoteState.FollowUp,
        [(VoteState.Revealed, VoteEvent.Escalate)] = VoteState.Escalated,
        [(VoteState.Revealed, VoteEvent.Withdraw)] = VoteState.Withdrawn,
        [(VoteState.FollowUp, VoteEvent.Decide)] = VoteState.Decided,
        [(VoteState.FollowUp, VoteEvent.Escalate)] = VoteState.Escalated,
        [(VoteState.FollowUp, VoteEvent.Withdraw)] = VoteState.Withdrawn,
        [(VoteState.Escalated, VoteEvent.OwnerResolved)] = VoteState.Decided,
        [(VoteState.Escalated, VoteEvent.Withdraw)] = VoteState.Withdrawn,
    };

    [Fact]
    public void Every_state_and_event_pair_matches_the_legal_table()
    {
        foreach (var state in Enum.GetValues<VoteState>())
        {
            foreach (var evt in Enum.GetValues<VoteEvent>())
            {
                var expected = _legal.TryGetValue((state, evt), out var target);

                var allowed = VoteStateMachine.TryNext(state, evt, out var next);

                Assert.True(expected == allowed, $"{state} + {evt}: expected allowed={expected}, got {allowed}");
                Assert.Equal(expected ? target : state, next);
            }
        }
    }

    [Theory]
    [InlineData(VoteState.Decided)]
    [InlineData(VoteState.Withdrawn)]
    public void Terminal_states_have_no_transitions(VoteState state)
    {
        Assert.True(VoteStateMachine.IsTerminal(state));
        foreach (var evt in Enum.GetValues<VoteEvent>())
        {
            Assert.False(VoteStateMachine.TryNext(state, evt, out _));
        }
    }

    [Theory]
    [InlineData(VoteState.Collecting, false)]
    [InlineData(VoteState.Revealed, false)]
    [InlineData(VoteState.FollowUp, false)]
    [InlineData(VoteState.Escalated, false)]
    public void Non_terminal_states_are_not_terminal(VoteState state, bool expected) =>
        Assert.Equal(expected, VoteStateMachine.IsTerminal(state));

    [Fact]
    public void Next_throws_for_a_disallowed_event()
    {
        var error = Assert.Throws<InvalidOperationException>(() => VoteStateMachine.Next(VoteState.Revealed, VoteEvent.AllEligibleCast));

        Assert.Contains("AllEligibleCast", error.Message);
        Assert.Contains("Revealed", error.Message);
    }

    [Fact]
    public void Bigger_change_split_path_runs_reveal_follow_up_then_escalation()
    {
        var state = VoteStateMachine.Next(VoteState.Collecting, VoteEvent.AllEligibleCast);
        Assert.Equal(VoteState.Revealed, state);

        state = VoteStateMachine.Next(state, VoteStateMachine.EventFor(TallyDecisionKind.FollowUpRequired));
        Assert.Equal(VoteState.FollowUp, state);

        state = VoteStateMachine.Next(state, VoteStateMachine.EventFor(TallyDecisionKind.Escalated));
        Assert.Equal(VoteState.Escalated, state);

        state = VoteStateMachine.Next(state, VoteEvent.OwnerResolved);
        Assert.Equal(VoteState.Decided, state);
    }

    [Fact]
    public void Escalated_votes_cannot_be_decided_by_the_tally()
    {
        Assert.False(VoteStateMachine.TryNext(VoteState.Escalated, VoteEvent.Decide, out _));
    }

    [Theory]
    [InlineData(VoteState.Collecting, BallotPhase.Independent, true)]
    [InlineData(VoteState.Collecting, BallotPhase.FollowUp, false)]
    [InlineData(VoteState.Revealed, BallotPhase.Independent, false)]
    [InlineData(VoteState.Revealed, BallotPhase.FollowUp, false)]
    [InlineData(VoteState.FollowUp, BallotPhase.Independent, false)]
    [InlineData(VoteState.FollowUp, BallotPhase.FollowUp, true)]
    [InlineData(VoteState.Escalated, BallotPhase.FollowUp, false)]
    [InlineData(VoteState.Decided, BallotPhase.Independent, false)]
    [InlineData(VoteState.Withdrawn, BallotPhase.FollowUp, false)]
    public void Ballots_are_accepted_only_in_their_phase(VoteState state, BallotPhase phase, bool expected) =>
        Assert.Equal(expected, VoteStateMachine.CanAcceptBallot(state, phase));

    [Fact]
    public void Deadline_with_quorum_reveals_without_using_an_extension()
    {
        var evt = VoteStateMachine.DeadlineEvent(quorumMet: true, extensionsUsed: 0, extensionsMax: 1);

        Assert.Equal(VoteEvent.DeadlineWithQuorum, evt);
        Assert.Equal(VoteState.Revealed, VoteStateMachine.Next(VoteState.Collecting, evt));
    }

    [Fact]
    public void Missing_quorum_gets_exactly_one_extension_then_escalates()
    {
        var first = VoteStateMachine.DeadlineEvent(quorumMet: false, extensionsUsed: 0, extensionsMax: 1);
        Assert.Equal(VoteEvent.DeadlineExtended, first);
        Assert.Equal(VoteState.Collecting, VoteStateMachine.Next(VoteState.Collecting, first));

        var second = VoteStateMachine.DeadlineEvent(quorumMet: false, extensionsUsed: 1, extensionsMax: 1);
        Assert.Equal(VoteEvent.DeadlineWithoutQuorum, second);
        Assert.Equal(VoteState.Escalated, VoteStateMachine.Next(VoteState.Collecting, second));
    }

    [Fact]
    public void Zero_allowed_extensions_escalates_at_the_first_missed_deadline()
    {
        var evt = VoteStateMachine.DeadlineEvent(quorumMet: false, extensionsUsed: 0, extensionsMax: 0);

        Assert.Equal(VoteEvent.DeadlineWithoutQuorum, evt);
    }

    [Theory]
    [InlineData(TallyDecisionKind.Decided, VoteEvent.Decide)]
    [InlineData(TallyDecisionKind.FollowUpRequired, VoteEvent.RequireFollowUp)]
    [InlineData(TallyDecisionKind.Escalated, VoteEvent.Escalate)]
    public void Tally_results_map_to_their_events(TallyDecisionKind kind, VoteEvent expected) =>
        Assert.Equal(expected, VoteStateMachine.EventFor(kind));
}
