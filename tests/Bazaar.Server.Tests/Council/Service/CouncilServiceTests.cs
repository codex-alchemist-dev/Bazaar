using System.Text.Json;
using Bazaar.Server.Council;
using Bazaar.Server.Council.Service;

namespace Bazaar.Server.Tests.Council.Service;

public class CouncilServiceTests
{
    private static readonly string[] _threeVoters = ["cal", "dot", "eve"];
    private static readonly string[] _reversedVoters = ["eve", "cal", "dot"];

    private static async Task<CouncilVoteRecord> VoteOf(CouncilHarness h, Guid voteId) =>
        (await h.Store.FindVoteAsync(voteId, CancellationToken.None))!;

    private static async Task<Guid> OpenThreeVoter(CouncilHarness h, MotionClass motionClass = MotionClass.Ordinary)
    {
        h.AddThreeVoters();
        return await h.CreateService().OpenAsync("ada", h.Motion(motionClass: motionClass), CancellationToken.None);
    }

    [Fact]
    public async Task Open_snapshots_electorate_and_excludes_proposer_author_subject_and_owner()
    {
        var h = new CouncilHarness();
        h.AddAgents("ada", "bea", "cal", "dot", "eve", "fin", "gil");
        h.AddOwner();
        var id = await h.CreateService().OpenAsync(
            "ada",
            h.Motion(author: "bea", membershipSubject: "gil"),
            CancellationToken.None);

        var vote = await VoteOf(h, id);
        Assert.Equal(VoteState.Collecting, vote.State);
        Assert.Equal(1, vote.Version);
        Assert.Equal(["cal", "dot", "eve", "fin"], vote.EligibleVoterIds.Order().ToArray());
    }

    [Fact]
    public async Task Open_by_someone_other_than_the_proposer_is_rejected()
    {
        var h = new CouncilHarness();
        h.AddThreeVoters();
        var service = h.CreateService();
        await Assert.ThrowsAsync<CouncilRejectedException>(() =>
            service.OpenAsync("cal", h.Motion(), CancellationToken.None));
    }

    [Fact]
    public async Task Open_checks_options_against_the_vote_kind()
    {
        var h = new CouncilHarness();
        h.AddThreeVoters();
        var service = h.CreateService();
        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.OpenAsync("ada", h.Motion(options: ["yes"]), CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.OpenAsync("ada", h.Motion(VoteKind.OptionChoice, options: ["only"]), CancellationToken.None));
    }

    [Fact]
    public void Invalid_options_are_rejected_at_construction()
    {
        var h = new CouncilHarness { Options = new CouncilOptions { QuorumMin = 0 } };
        Assert.Throws<ArgumentException>(() => h.CreateService());
    }

    [Fact]
    public async Task Ballot_from_a_non_eligible_voter_is_rejected()
    {
        var h = new CouncilHarness();
        var id = await OpenThreeVoter(h);
        var service = h.CreateService();

        await Assert.ThrowsAsync<CouncilRejectedException>(() =>
            service.SubmitBallotAsync(id, "ada", Ballots.Independent("ada", "yes"), CancellationToken.None));
        await Assert.ThrowsAsync<CouncilRejectedException>(() =>
            service.SubmitBallotAsync(id, "owner", Ballots.Independent("owner", "yes"), CancellationToken.None));
    }

    [Fact]
    public async Task Duplicate_independent_ballot_is_rejected()
    {
        var h = new CouncilHarness();
        var id = await OpenThreeVoter(h);
        var service = h.CreateService();
        await service.SubmitBallotAsync(id, "cal", Ballots.Independent("cal", "yes"), CancellationToken.None);

        await Assert.ThrowsAsync<CouncilRejectedException>(() =>
            service.SubmitBallotAsync(id, "cal", Ballots.Independent("cal", "no"), CancellationToken.None));
    }

    [Fact]
    public async Task Ballot_reasoning_needs_the_minimum_sentences_and_stays_within_length()
    {
        var h = new CouncilHarness();
        var id = await OpenThreeVoter(h);
        var service = h.CreateService();

        await Assert.ThrowsAsync<CouncilRejectedException>(() =>
            service.SubmitBallotAsync(id, "cal", new Ballot("cal", "yes", "Only one sentence.", BallotPhase.Independent), CancellationToken.None));
        await Assert.ThrowsAsync<CouncilRejectedException>(() =>
            service.SubmitBallotAsync(id, "cal", new Ballot("cal", "yes", new string('x', 2001) + ". Two.", BallotPhase.Independent), CancellationToken.None));

        await service.SubmitBallotAsync(id, "cal", Ballots.Independent("cal", "yes"), CancellationToken.None);
    }

    [Fact]
    public async Task Ordinary_majority_decides_at_the_reveal()
    {
        var h = new CouncilHarness();
        var id = await OpenThreeVoter(h);
        var service = h.CreateService();
        await service.SubmitBallotAsync(id, "cal", Ballots.Independent("cal", "yes"), CancellationToken.None);
        await service.SubmitBallotAsync(id, "dot", Ballots.Independent("dot", "yes"), CancellationToken.None);
        await service.SubmitBallotAsync(id, "eve", Ballots.Independent("eve", "no"), CancellationToken.None);

        var vote = await VoteOf(h, id);
        Assert.Equal(VoteState.Decided, vote.State);
        Assert.Single(h.Writer.Writes);
        Assert.Equal(1, vote.VaultDecisionNo);
        var decision = h.Events.OfKind("council.decision").Single();
        Assert.Contains("\"choice\":\"yes\"", decision.PayloadJson);
        Assert.Contains("\"decided_by\":\"tally\"", decision.PayloadJson);
    }

    [Fact]
    public async Task Bigger_change_unanimous_decides_at_the_reveal()
    {
        var h = new CouncilHarness();
        var id = await OpenThreeVoter(h, MotionClass.BiggerChange);
        var service = h.CreateService();
        foreach (var voter in _threeVoters)
        {
            await service.SubmitBallotAsync(id, voter, Ballots.Independent(voter, "yes"), CancellationToken.None);
        }

        Assert.Equal(VoteState.Decided, (await VoteOf(h, id)).State);
    }

    [Fact]
    public async Task Bigger_change_split_goes_to_follow_up_and_then_decides_when_unanimous()
    {
        var h = new CouncilHarness();
        var id = await OpenThreeVoter(h, MotionClass.BiggerChange);
        var service = h.CreateService();
        await service.SubmitBallotAsync(id, "cal", Ballots.Independent("cal", "yes"), CancellationToken.None);
        await service.SubmitBallotAsync(id, "dot", Ballots.Independent("dot", "yes"), CancellationToken.None);
        await service.SubmitBallotAsync(id, "eve", Ballots.Independent("eve", "no"), CancellationToken.None);

        Assert.Equal(VoteState.FollowUp, (await VoteOf(h, id)).State);
        Assert.Empty(h.Events.OfKind("council.decision"));

        await service.SubmitBallotAsync(id, "cal", Ballots.FollowUp("cal", "yes"), CancellationToken.None);
        await service.SubmitBallotAsync(id, "dot", Ballots.FollowUp("dot", "yes"), CancellationToken.None);
        Assert.Equal(VoteState.FollowUp, (await VoteOf(h, id)).State);

        await service.SubmitBallotAsync(id, "eve", Ballots.FollowUp("eve", "yes"), CancellationToken.None);
        var vote = await VoteOf(h, id);
        Assert.Equal(VoteState.Decided, vote.State);
        Assert.Contains("\"decided_by\":\"tally\"", h.Events.OfKind("council.decision").Single().PayloadJson);
    }

    [Fact]
    public async Task Follow_up_tie_escalates_and_an_independent_split_has_no_second_round()
    {
        var h = new CouncilHarness();
        h.AddAgents("ada", "cal", "dot", "eve", "fin");
        var id = await h.CreateService().OpenAsync("ada", h.Motion(), CancellationToken.None);
        var service = h.CreateService();
        await service.SubmitBallotAsync(id, "cal", Ballots.Independent("cal", "yes"), CancellationToken.None);
        await service.SubmitBallotAsync(id, "dot", Ballots.Independent("dot", "yes"), CancellationToken.None);
        await service.SubmitBallotAsync(id, "eve", Ballots.Independent("eve", "no"), CancellationToken.None);
        await service.SubmitBallotAsync(id, "fin", Ballots.Independent("fin", "no"), CancellationToken.None);
        Assert.Equal(VoteState.FollowUp, (await VoteOf(h, id)).State);

        await service.SubmitBallotAsync(id, "cal", Ballots.FollowUp("cal", "yes"), CancellationToken.None);
        await service.SubmitBallotAsync(id, "dot", Ballots.FollowUp("dot", "yes"), CancellationToken.None);
        await service.SubmitBallotAsync(id, "eve", Ballots.FollowUp("eve", "no"), CancellationToken.None);
        await service.SubmitBallotAsync(id, "fin", Ballots.FollowUp("fin", "no"), CancellationToken.None);

        Assert.Equal(VoteState.Escalated, (await VoteOf(h, id)).State);
        Assert.Contains("escalated: tie", h.Events.OfKind("council.transition").Last().PayloadJson);
        Assert.Empty(h.Writer.Writes);
    }

    [Fact]
    public async Task Owner_resolves_an_escalation_only_with_a_listed_option()
    {
        var h = new CouncilHarness();
        h.AddThreeVoters();
        string[] options = ["alpha", "beta", "gamma"];
        var id = await h.CreateService().OpenAsync(
            "ada",
            h.Motion(VoteKind.OptionChoice, options: options),
            CancellationToken.None);
        var service = h.CreateService();
        await service.SubmitBallotAsync(id, "cal", Ballots.Independent("cal", "alpha"), CancellationToken.None);
        await service.SubmitBallotAsync(id, "dot", Ballots.Independent("dot", "beta"), CancellationToken.None);
        await service.SubmitBallotAsync(id, "eve", Ballots.Independent("eve", "gamma"), CancellationToken.None);
        await service.SubmitBallotAsync(id, "cal", Ballots.FollowUp("cal", "alpha"), CancellationToken.None);
        await service.SubmitBallotAsync(id, "dot", Ballots.FollowUp("dot", "beta"), CancellationToken.None);
        await service.SubmitBallotAsync(id, "eve", Ballots.FollowUp("eve", "gamma"), CancellationToken.None);
        Assert.Equal(VoteState.Escalated, (await VoteOf(h, id)).State);

        await Assert.ThrowsAsync<CouncilRejectedException>(() =>
            service.ResolveEscalationAsync(id, "delta", null, CancellationToken.None));
        await Assert.ThrowsAsync<CouncilRejectedException>(() =>
            service.ResolveEscalationAsync(id, ApprovalChoices.Yes, null, CancellationToken.None));
        Assert.Equal(VoteState.Escalated, (await VoteOf(h, id)).State);

        await service.ResolveEscalationAsync(id, "beta", "The owner sides with beta.", CancellationToken.None);
        Assert.Equal(VoteState.Decided, (await VoteOf(h, id)).State);
        Assert.Single(h.Writer.Writes);
        Assert.Contains("\"decided_by\":\"owner\"", h.Events.OfKind("council.decision").Single().PayloadJson);
        Assert.Contains("\"choice\":\"beta\"", h.Events.OfKind("council.decision").Single().PayloadJson);
    }

    [Fact]
    public async Task Owner_resolution_is_refused_unless_the_vote_is_escalated()
    {
        var h = new CouncilHarness();
        var id = await OpenThreeVoter(h);
        await Assert.ThrowsAsync<CouncilRejectedException>(() =>
            h.CreateService().ResolveEscalationAsync(id, ApprovalChoices.Yes, null, CancellationToken.None));
    }

    [Fact]
    public async Task Reveal_is_idempotent_under_a_repeated_call()
    {
        var h = new CouncilHarness();
        var id = await OpenThreeVoter(h, MotionClass.BiggerChange);
        // Ballots are written without the service so that the vote is not revealed yet; then the vote is put in
        // Revealed, as a crash after the reveal transition would leave it.
        foreach (var voter in _threeVoters)
        {
            h.Store.InsertBallotDirectly(new StoredBallot(id, voter, BallotPhase.Independent, "yes", Ballots.Reasoning, false, h.Clock.Now));
        }

        h.Store.SetState(id, VoteState.Revealed);
        var service = h.CreateService();
        await service.ProcessDeadlineAsync(id, CancellationToken.None);

        Assert.Equal(VoteState.Decided, (await VoteOf(h, id)).State);
        Assert.Single(h.Events.OfKind("council.reveal"));
        var eventsAfterFirst = h.Events.Events.Count;

        await service.ProcessDeadlineAsync(id, CancellationToken.None);
        await service.ProcessDeadlineAsync(id, CancellationToken.None);
        Assert.Equal(eventsAfterFirst, h.Events.Events.Count);
        Assert.Single(h.Events.OfKind("council.reveal"));
        Assert.Single(h.Writer.Writes);
    }

    [Fact]
    public async Task Late_ballot_after_the_reveal_is_rejected()
    {
        var h = new CouncilHarness { Options = new CouncilOptions { QuorumMin = 2 } };
        var id = await OpenThreeVoter(h);
        var service = h.CreateService();
        await service.SubmitBallotAsync(id, "cal", Ballots.Independent("cal", "yes"), CancellationToken.None);
        await service.SubmitBallotAsync(id, "dot", Ballots.Independent("dot", "yes"), CancellationToken.None);

        h.Clock.Now = (await VoteOf(h, id)).DeadlineAt.AddMinutes(1);
        await service.ProcessDeadlineAsync(id, CancellationToken.None);
        Assert.Equal(VoteState.Decided, (await VoteOf(h, id)).State);

        await Assert.ThrowsAsync<CouncilRejectedException>(() =>
            service.SubmitBallotAsync(id, "eve", Ballots.Independent("eve", "no"), CancellationToken.None));
    }

    [Fact]
    public async Task Deadline_without_quorum_extends_once_then_escalates()
    {
        var h = new CouncilHarness();
        h.AddAgents("ada", "cal", "dot", "eve", "fin");
        var id = await h.CreateService().OpenAsync("ada", h.Motion(), CancellationToken.None);
        var service = h.CreateService();
        await service.SubmitBallotAsync(id, "cal", Ballots.Independent("cal", "yes"), CancellationToken.None);

        await service.ProcessDeadlineAsync(id, CancellationToken.None);
        Assert.Equal(1, (await VoteOf(h, id)).Version);

        var firstDeadline = (await VoteOf(h, id)).DeadlineAt;
        h.Clock.Now = firstDeadline.AddMinutes(1);
        await service.ProcessDeadlineAsync(id, CancellationToken.None);
        var extended = await VoteOf(h, id);
        Assert.Equal(VoteState.Collecting, extended.State);
        Assert.Equal(1, extended.Extensions);
        Assert.Equal(h.Clock.Now.AddHours(72), extended.DeadlineAt);

        h.Clock.Now = extended.DeadlineAt.AddMinutes(1);
        await service.ProcessDeadlineAsync(id, CancellationToken.None);
        Assert.Equal(VoteState.Escalated, (await VoteOf(h, id)).State);
        Assert.Contains("no quorum after extension", h.Events.OfKind("council.transition").Last().PayloadJson);
    }

    [Fact]
    public async Task Deadline_before_it_passes_changes_nothing()
    {
        var h = new CouncilHarness();
        var id = await OpenThreeVoter(h);
        var before = await VoteOf(h, id);

        await h.CreateService().ProcessDeadlineAsync(id, CancellationToken.None);

        Assert.Equal(before, await VoteOf(h, id));
        Assert.Single(h.Events.Events);
    }

    [Fact]
    public async Task Version_conflicts_are_retried_then_raised_after_three_attempts()
    {
        var h = new CouncilHarness();
        var id = await OpenThreeVoter(h);
        var service = h.CreateService();
        await service.SubmitBallotAsync(id, "cal", Ballots.Independent("cal", "yes"), CancellationToken.None);
        await service.SubmitBallotAsync(id, "dot", Ballots.Independent("dot", "yes"), CancellationToken.None);

        h.Store.ForcedMismatches = 2;
        await service.SubmitBallotAsync(id, "eve", Ballots.Independent("eve", "yes"), CancellationToken.None);
        Assert.Equal(VoteState.Decided, (await VoteOf(h, id)).State);

        var second = await OpenThreeVoter(new CouncilHarness());
        Assert.NotEqual(Guid.Empty, second);
    }

    [Fact]
    public async Task Version_conflict_on_every_attempt_raises_concurrency_exception()
    {
        var h = new CouncilHarness();
        var id = await OpenThreeVoter(h);
        var service = h.CreateService();
        await service.SubmitBallotAsync(id, "cal", Ballots.Independent("cal", "yes"), CancellationToken.None);
        await service.SubmitBallotAsync(id, "dot", Ballots.Independent("dot", "yes"), CancellationToken.None);

        h.Store.ForcedMismatches = 3;
        await Assert.ThrowsAsync<ConcurrencyException>(() =>
            service.SubmitBallotAsync(id, "eve", Ballots.Independent("eve", "yes"), CancellationToken.None));
        Assert.Equal(VoteState.Collecting, (await VoteOf(h, id)).State);
    }

    [Fact]
    public async Task Withdraw_by_a_non_proposer_is_rejected_and_by_the_proposer_succeeds()
    {
        var h = new CouncilHarness();
        var id = await OpenThreeVoter(h);
        var service = h.CreateService();

        await Assert.ThrowsAsync<CouncilRejectedException>(() =>
            service.WithdrawAsync(id, "cal", CancellationToken.None));
        Assert.Equal(VoteState.Collecting, (await VoteOf(h, id)).State);

        await service.WithdrawAsync(id, "ada", CancellationToken.None);
        Assert.Equal(VoteState.Withdrawn, (await VoteOf(h, id)).State);
        await Assert.ThrowsAsync<CouncilRejectedException>(() =>
            service.WithdrawAsync(id, "ada", CancellationToken.None));
    }

    [Fact]
    public async Task Owner_can_withdraw_a_vote_they_did_not_propose()
    {
        var h = new CouncilHarness();
        var id = await OpenThreeVoter(h);
        await h.CreateService().WithdrawAsync(id, "owner", CancellationToken.None);
        Assert.Equal(VoteState.Withdrawn, (await VoteOf(h, id)).State);
    }

    [Fact]
    public async Task Each_transition_writes_one_transition_event_with_its_version_key()
    {
        var h = new CouncilHarness();
        var id = await OpenThreeVoter(h, MotionClass.BiggerChange);
        var service = h.CreateService();
        await service.SubmitBallotAsync(id, "cal", Ballots.Independent("cal", "yes"), CancellationToken.None);
        await service.SubmitBallotAsync(id, "dot", Ballots.Independent("dot", "yes"), CancellationToken.None);
        await service.SubmitBallotAsync(id, "eve", Ballots.Independent("eve", "no"), CancellationToken.None);
        await service.SubmitBallotAsync(id, "cal", Ballots.FollowUp("cal", "yes"), CancellationToken.None);
        await service.SubmitBallotAsync(id, "dot", Ballots.FollowUp("dot", "yes"), CancellationToken.None);
        await service.SubmitBallotAsync(id, "eve", Ballots.FollowUp("eve", "yes"), CancellationToken.None);

        var vote = await VoteOf(h, id);
        Assert.Equal(VoteState.Decided, vote.State);
        Assert.Equal(4, vote.Version);

        var transitionKeys = h.Events.OfKind("council.transition").Select(evt => evt.IdempotencyKey).ToArray();
        Assert.Equal(
            [$"transition:{id:D}:2", $"transition:{id:D}:3", $"transition:{id:D}:4"],
            transitionKeys);
        Assert.Equal([$"open:{id:D}"], h.Events.OfKind("council.open").Select(evt => evt.IdempotencyKey));
        Assert.Equal([$"reveal:{id:D}"], h.Events.OfKind("council.reveal").Select(evt => evt.IdempotencyKey));
        Assert.Equal([$"decision:{id:D}"], h.Events.OfKind("council.decision").Select(evt => evt.IdempotencyKey));
        Assert.Contains("\"to\":\"follow_up\"", h.Events.OfKind("council.transition").ElementAt(1).PayloadJson);
    }

    [Fact]
    public async Task Open_payload_carries_no_reasoning_and_the_reveal_does()
    {
        var h = new CouncilHarness();
        var id = await OpenThreeVoter(h);
        var openPayload = h.Events.OfKind("council.open").Single().PayloadJson;
        Assert.DoesNotContain("reason", openPayload, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Ballots.Reasoning, openPayload, StringComparison.Ordinal);

        var service = h.CreateService();
        foreach (var voter in _threeVoters)
        {
            await service.SubmitBallotAsync(id, voter, Ballots.Independent(voter, "yes"), CancellationToken.None);
        }

        Assert.Contains(Ballots.Reasoning, h.Events.OfKind("council.reveal").Single().PayloadJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Reveal_reasons_appear_in_the_stored_reason_order()
    {
        var h = new CouncilHarness();
        var id = await OpenThreeVoter(h);
        var service = h.CreateService();
        foreach (var voter in _reversedVoters)
        {
            await service.SubmitBallotAsync(id, voter, Ballots.Independent(voter, "yes"), CancellationToken.None);
        }

        using var reveal = JsonDocument.Parse(h.Events.OfKind("council.reveal").Single().PayloadJson);
        var root = reveal.RootElement;
        var order = root.GetProperty("reason_order").EnumerateArray().Select(item => item.GetString()).ToArray();
        var reasons = root.GetProperty("reasons").EnumerateArray()
            .Select(item => item.GetProperty("voter_id").GetString()).ToArray();

        Assert.Equal(_threeVoters, order.Order().ToArray());
        Assert.Equal(order, reasons);
        Assert.Equal((await VoteOf(h, id)).RevealSeed, root.GetProperty("reveal_seed").GetInt64());
    }

    [Fact]
    public async Task Follow_up_disabled_escalates_a_split_bigger_change_without_a_second_round()
    {
        var h = new CouncilHarness { Options = new CouncilOptions { FollowupEnabled = false } };
        var id = await OpenThreeVoter(h, MotionClass.BiggerChange);
        var service = h.CreateService();
        await service.SubmitBallotAsync(id, "cal", Ballots.Independent("cal", "yes"), CancellationToken.None);
        await service.SubmitBallotAsync(id, "dot", Ballots.Independent("dot", "yes"), CancellationToken.None);
        await service.SubmitBallotAsync(id, "eve", Ballots.Independent("eve", "no"), CancellationToken.None);

        Assert.Equal(VoteState.Escalated, (await VoteOf(h, id)).State);
        Assert.Contains("escalated: not_unanimous", h.Events.OfKind("council.transition").Last().PayloadJson);
    }

    [Fact]
    public async Task All_gc_members_electorate_blocks_unanimity_when_a_member_cannot_vote()
    {
        var h = new CouncilHarness { Options = new CouncilOptions { UnanimityElectorate = UnanimityElectorate.AllGcMembers } };
        var id = await OpenThreeVoter(h, MotionClass.BiggerChange);
        var service = h.CreateService();
        foreach (var voter in _threeVoters)
        {
            await service.SubmitBallotAsync(id, voter, Ballots.Independent(voter, "yes"), CancellationToken.None);
        }

        // The owner and the proposer are GC members who cannot vote, so the all-members unanimity is never met.
        Assert.Equal(VoteState.FollowUp, (await VoteOf(h, id)).State);
        foreach (var voter in _threeVoters)
        {
            await service.SubmitBallotAsync(id, voter, Ballots.FollowUp(voter, "yes"), CancellationToken.None);
        }

        Assert.Equal(VoteState.Escalated, (await VoteOf(h, id)).State);
        Assert.Contains("escalated: not_unanimous", h.Events.OfKind("council.transition").Last().PayloadJson);
    }
}
