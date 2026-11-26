using Bazaar.Server.Council;
using Bazaar.Server.Council.Service;
using Microsoft.Extensions.Logging.Abstractions;

namespace Bazaar.Server.Council.Tests.Service;

public class CouncilDeadlineSweeperTests
{
    private static CouncilDeadlineSweeper Sweeper(
        CouncilHarness h,
        ICouncilService? council = null,
        CouncilSweepOptions? options = null) =>
        new(h.Store, council ?? h.CreateService(), h.Clock, options ?? new CouncilSweepOptions(), NullLogger<CouncilDeadlineSweeper>.Instance);

    /// <summary>A service whose council.decision append fails while the events fake is armed, as a crash would.</summary>
    private static (CouncilService Service, CrashingGcEvents Crash) CrashingService(CouncilHarness h)
    {
        var crash = new CrashingGcEvents(h.Events, "council.decision");
        return (new CouncilService(h.Store, crash, h.Membership, h.Options, h.Clock, h.Writer), crash);
    }

    private static async Task<CouncilVoteRecord> VoteOf(CouncilHarness h, Guid voteId) =>
        (await h.Store.FindVoteAsync(voteId, CancellationToken.None))!;

    /// <summary>Writes three independent ballots (yes, yes, no) directly and marks the vote Revealed, as a crash would leave it.</summary>
    private static void StickInRevealed(CouncilHarness h, Guid id)
    {
        h.Store.InsertBallotDirectly(new StoredBallot(id, "cal", BallotPhase.Independent, "yes", Ballots.Reasoning, false, h.Clock.Now));
        h.Store.InsertBallotDirectly(new StoredBallot(id, "dot", BallotPhase.Independent, "yes", Ballots.Reasoning, false, h.Clock.Now));
        h.Store.InsertBallotDirectly(new StoredBallot(id, "eve", BallotPhase.Independent, "no", Ballots.Reasoning, false, h.Clock.Now));
        h.Store.SetState(id, VoteState.Revealed);
    }

    [Fact]
    public async Task Deadline_with_quorum_reveals_and_decides_the_vote()
    {
        var h = new CouncilHarness();
        h.AddAgents("ada", "cal", "dot", "eve", "fin");
        var service = h.CreateService();
        var id = await service.OpenAsync("ada", h.Motion(), CancellationToken.None);
        await service.SubmitBallotAsync(id, "cal", Ballots.Independent("cal", "yes"), CancellationToken.None);
        await service.SubmitBallotAsync(id, "dot", Ballots.Independent("dot", "yes"), CancellationToken.None);
        await service.SubmitBallotAsync(id, "eve", Ballots.Independent("eve", "no"), CancellationToken.None);
        Assert.Equal(VoteState.Collecting, (await VoteOf(h, id)).State);

        h.Clock.Now = (await VoteOf(h, id)).DeadlineAt.AddMinutes(1);
        var result = await Sweeper(h).RunOnceAsync(CancellationToken.None);

        // The vote was decided in this pass, so the same pass also re-drives its decision; the event key makes that a no-op.
        Assert.Equal(new CouncilSweepResult(1, 1, 0), result);
        Assert.Equal(VoteState.Decided, (await VoteOf(h, id)).State);
        Assert.Single(h.Events.OfKind("council.reveal"));
        Assert.Single(h.Events.OfKind("council.decision"));
    }

    [Fact]
    public async Task Deadline_without_quorum_extends_once_then_the_next_pass_escalates()
    {
        var h = new CouncilHarness();
        h.AddAgents("ada", "cal", "dot", "eve", "fin");
        var service = h.CreateService();
        var id = await service.OpenAsync("ada", h.Motion(), CancellationToken.None);
        await service.SubmitBallotAsync(id, "cal", Ballots.Independent("cal", "yes"), CancellationToken.None);
        var sweeper = Sweeper(h);

        h.Clock.Now = (await VoteOf(h, id)).DeadlineAt.AddMinutes(1);
        Assert.Equal(new CouncilSweepResult(1, 0, 0), await sweeper.RunOnceAsync(CancellationToken.None));
        var extended = await VoteOf(h, id);
        Assert.Equal(VoteState.Collecting, extended.State);
        Assert.Equal(1, extended.Extensions);

        h.Clock.Now = extended.DeadlineAt.AddMinutes(1);
        Assert.Equal(new CouncilSweepResult(1, 0, 0), await sweeper.RunOnceAsync(CancellationToken.None));
        Assert.Equal(VoteState.Escalated, (await VoteOf(h, id)).State);
    }

    [Fact]
    public async Task A_vote_before_its_deadline_and_a_withdrawn_vote_are_left_alone()
    {
        var h = new CouncilHarness();
        h.AddThreeVoters();
        var service = h.CreateService();
        var open = await service.OpenAsync("ada", h.Motion(), CancellationToken.None);
        var withdrawn = await service.OpenAsync("ada", h.Motion(), CancellationToken.None);
        await service.WithdrawAsync(withdrawn, "ada", CancellationToken.None);
        var eventsBefore = h.Events.Events.Count;

        var result = await Sweeper(h).RunOnceAsync(CancellationToken.None);

        Assert.Equal(new CouncilSweepResult(0, 0, 0), result);
        Assert.Equal(VoteState.Collecting, (await VoteOf(h, open)).State);
        Assert.Equal(eventsBefore, h.Events.Events.Count);
    }

    [Fact]
    public async Task A_vote_stuck_in_revealed_after_a_crash_is_completed_by_the_pass()
    {
        var h = new CouncilHarness();
        h.AddThreeVoters();
        var id = await h.CreateService().OpenAsync("ada", h.Motion(), CancellationToken.None);
        StickInRevealed(h, id);

        var result = await Sweeper(h).RunOnceAsync(CancellationToken.None);

        // The vote was decided in this pass, so the same pass also re-drives its decision; the event key makes that a no-op.
        Assert.Equal(new CouncilSweepResult(1, 1, 0), result);
        Assert.Equal(VoteState.Decided, (await VoteOf(h, id)).State);
        Assert.Single(h.Events.OfKind("council.reveal"));
        Assert.Equal(3, h.Events.OfKind("council.reason").Count());
        Assert.Single(h.Events.OfKind("council.decision"));
    }

    [Fact]
    public async Task Repeating_a_pass_after_a_decision_writes_no_new_events()
    {
        var h = new CouncilHarness();
        h.AddThreeVoters();
        var id = await h.CreateService().OpenAsync("ada", h.Motion(), CancellationToken.None);
        StickInRevealed(h, id);
        var sweeper = Sweeper(h);
        await sweeper.RunOnceAsync(CancellationToken.None);
        var eventsAfterFirst = h.Events.Events.Count;

        var second = await sweeper.RunOnceAsync(CancellationToken.None);

        Assert.Equal(new CouncilSweepResult(0, 1, 0), second);
        Assert.Equal(eventsAfterFirst, h.Events.Events.Count);
        Assert.Single(h.Writer.Writes);
    }

    [Fact]
    public async Task A_tally_decision_lost_to_a_crash_is_re_driven_once_without_a_second_vault_file()
    {
        var h = new CouncilHarness();
        h.AddThreeVoters();
        var (service, crash) = CrashingService(h);
        var id = await service.OpenAsync("ada", h.Motion(), CancellationToken.None);
        await service.SubmitBallotAsync(id, "cal", Ballots.Independent("cal", "yes"), CancellationToken.None);
        await service.SubmitBallotAsync(id, "dot", Ballots.Independent("dot", "yes"), CancellationToken.None);
        await Assert.ThrowsAsync<IOException>(() =>
            service.SubmitBallotAsync(id, "eve", Ballots.Independent("eve", "no"), CancellationToken.None));
        Assert.Equal(VoteState.Decided, (await VoteOf(h, id)).State);
        Assert.Empty(h.Events.OfKind("council.decision"));

        crash.Armed = false;
        var result = await Sweeper(h, service).RunOnceAsync(CancellationToken.None);

        Assert.Equal(new CouncilSweepResult(0, 1, 0), result);
        var decision = Assert.Single(h.Events.OfKind("council.decision"));
        Assert.Contains("\"decided_by\":\"tally\"", decision.PayloadJson);
        Assert.Contains("\"choice\":\"yes\"", decision.PayloadJson);
        Assert.Single(h.Writer.Writes);
    }

    [Fact]
    public async Task A_follow_up_tally_lost_to_a_crash_is_re_driven_on_the_follow_up_ballots()
    {
        var h = new CouncilHarness();
        h.AddThreeVoters();
        var (service, crash) = CrashingService(h);
        var id = await service.OpenAsync("ada", h.Motion(motionClass: MotionClass.BiggerChange), CancellationToken.None);
        await service.SubmitBallotAsync(id, "cal", Ballots.Independent("cal", "yes"), CancellationToken.None);
        await service.SubmitBallotAsync(id, "dot", Ballots.Independent("dot", "yes"), CancellationToken.None);
        await service.SubmitBallotAsync(id, "eve", Ballots.Independent("eve", "no"), CancellationToken.None);
        await service.SubmitBallotAsync(id, "cal", Ballots.FollowUp("cal", "yes"), CancellationToken.None);
        await service.SubmitBallotAsync(id, "dot", Ballots.FollowUp("dot", "yes"), CancellationToken.None);
        await Assert.ThrowsAsync<IOException>(() =>
            service.SubmitBallotAsync(id, "eve", Ballots.FollowUp("eve", "yes"), CancellationToken.None));
        Assert.Equal(VoteState.Decided, (await VoteOf(h, id)).State);

        crash.Armed = false;
        await service.RedriveDecisionAsync(id, CancellationToken.None);

        var decision = Assert.Single(h.Events.OfKind("council.decision"));
        Assert.Contains("\"choice\":\"yes\"", decision.PayloadJson);
        Assert.Contains("\"followup_choice\":\"yes\"", decision.PayloadJson);
    }

    [Fact]
    public async Task An_owner_decision_lost_to_a_crash_is_re_driven_with_the_owner_choice_from_the_transition()
    {
        var h = new CouncilHarness();
        h.AddThreeVoters();
        string[] options = ["alpha", "beta", "gamma"];
        var (service, crash) = CrashingService(h);
        var id = await service.OpenAsync("ada", h.Motion(VoteKind.OptionChoice, options: options), CancellationToken.None);
        await service.SubmitBallotAsync(id, "cal", Ballots.Independent("cal", "alpha"), CancellationToken.None);
        await service.SubmitBallotAsync(id, "dot", Ballots.Independent("dot", "beta"), CancellationToken.None);
        await service.SubmitBallotAsync(id, "eve", Ballots.Independent("eve", "gamma"), CancellationToken.None);
        await service.SubmitBallotAsync(id, "cal", Ballots.FollowUp("cal", "alpha"), CancellationToken.None);
        await service.SubmitBallotAsync(id, "dot", Ballots.FollowUp("dot", "beta"), CancellationToken.None);
        await service.SubmitBallotAsync(id, "eve", Ballots.FollowUp("eve", "gamma"), CancellationToken.None);
        Assert.Equal(VoteState.Escalated, (await VoteOf(h, id)).State);
        await Assert.ThrowsAsync<IOException>(() =>
            service.ResolveEscalationAsync(id, "beta", "The owner sides with beta.", CancellationToken.None));
        Assert.Equal(VoteState.Decided, (await VoteOf(h, id)).State);

        crash.Armed = false;
        await Sweeper(h, service).RunOnceAsync(CancellationToken.None);

        var decision = Assert.Single(h.Events.OfKind("council.decision"));
        Assert.Contains("\"decided_by\":\"owner\"", decision.PayloadJson);
        Assert.Contains("\"choice\":\"beta\"", decision.PayloadJson);
        Assert.Contains("\"owner_reasoning\":null", decision.PayloadJson);
    }

    [Fact]
    public async Task Decision_recovery_is_off_when_its_lookback_is_zero()
    {
        var h = new CouncilHarness();
        h.AddThreeVoters();
        var (service, crash) = CrashingService(h);
        var id = await service.OpenAsync("ada", h.Motion(), CancellationToken.None);
        await service.SubmitBallotAsync(id, "cal", Ballots.Independent("cal", "yes"), CancellationToken.None);
        await service.SubmitBallotAsync(id, "dot", Ballots.Independent("dot", "yes"), CancellationToken.None);
        await Assert.ThrowsAsync<IOException>(() =>
            service.SubmitBallotAsync(id, "eve", Ballots.Independent("eve", "no"), CancellationToken.None));
        crash.Armed = false;

        var result = await Sweeper(h, service, new CouncilSweepOptions { DecisionRecoveryLookback = TimeSpan.Zero })
            .RunOnceAsync(CancellationToken.None);

        Assert.Equal(new CouncilSweepResult(0, 0, 0), result);
        Assert.Empty(h.Events.OfKind("council.decision"));
    }

    [Fact]
    public async Task A_decision_older_than_the_lookback_is_not_re_driven()
    {
        var h = new CouncilHarness();
        h.AddThreeVoters();
        var (service, crash) = CrashingService(h);
        var id = await service.OpenAsync("ada", h.Motion(), CancellationToken.None);
        await service.SubmitBallotAsync(id, "cal", Ballots.Independent("cal", "yes"), CancellationToken.None);
        await service.SubmitBallotAsync(id, "dot", Ballots.Independent("dot", "yes"), CancellationToken.None);
        await Assert.ThrowsAsync<IOException>(() =>
            service.SubmitBallotAsync(id, "eve", Ballots.Independent("eve", "no"), CancellationToken.None));
        crash.Armed = false;
        h.Clock.Now = h.Clock.Now.AddHours(2);

        var result = await Sweeper(h, service).RunOnceAsync(CancellationToken.None);

        Assert.Equal(new CouncilSweepResult(0, 0, 0), result);
        Assert.Empty(h.Events.OfKind("council.decision"));
    }

    [Fact]
    public async Task A_failing_vote_is_counted_and_the_pass_goes_on()
    {
        var h = new CouncilHarness();
        h.AddAgents("ada", "cal", "dot", "eve", "fin");
        var service = h.CreateService();
        var broken = await service.OpenAsync("ada", h.Motion(), CancellationToken.None);
        var healthy = await service.OpenAsync("ada", h.Motion(), CancellationToken.None);
        h.Clock.Now = h.Clock.Now.AddHours(73);

        var result = await Sweeper(h, new FailingCouncil(service, broken)).RunOnceAsync(CancellationToken.None);

        Assert.Equal(new CouncilSweepResult(1, 0, 1), result);
        Assert.Equal(1, (await VoteOf(h, healthy)).Extensions);
        Assert.Equal(0, (await VoteOf(h, broken)).Extensions);
    }

    [Fact]
    public async Task Each_pass_is_bounded_by_the_batch_size()
    {
        var h = new CouncilHarness();
        h.AddAgents("ada", "cal", "dot", "eve", "fin");
        var service = h.CreateService();
        var first = await service.OpenAsync("ada", h.Motion(), CancellationToken.None);
        var second = await service.OpenAsync("ada", h.Motion(), CancellationToken.None);
        h.Clock.Now = h.Clock.Now.AddHours(73);
        var sweeper = Sweeper(h, options: new CouncilSweepOptions { BatchSize = 1 });

        // Both votes share a deadline and the same clock time, so the order between them is not fixed. The test checks
        // only the counts: one vote per pass, and both handled after two passes.
        Assert.Equal(new CouncilSweepResult(1, 0, 0), await sweeper.RunOnceAsync(CancellationToken.None));
        var extendedAfterFirst = (await VoteOf(h, first)).Extensions + (await VoteOf(h, second)).Extensions;
        Assert.Equal(1, extendedAfterFirst);

        Assert.Equal(new CouncilSweepResult(1, 0, 0), await sweeper.RunOnceAsync(CancellationToken.None));
        Assert.Equal(1, (await VoteOf(h, first)).Extensions);
        Assert.Equal(1, (await VoteOf(h, second)).Extensions);
    }

    [Fact]
    public async Task A_cancelled_pass_stops_between_votes()
    {
        var h = new CouncilHarness();
        h.AddAgents("ada", "cal", "dot", "eve", "fin");
        await h.CreateService().OpenAsync("ada", h.Motion(), CancellationToken.None);
        h.Clock.Now = h.Clock.Now.AddHours(73);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAsync<OperationCanceledException>(() => Sweeper(h).RunOnceAsync(cts.Token));
    }

    [Fact]
    public void Invalid_sweep_options_are_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new CouncilSweepOptions { BatchSize = 0 }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new CouncilSweepOptions { Interval = TimeSpan.Zero }.Validate());
        var h = new CouncilHarness();
        Assert.Throws<ArgumentOutOfRangeException>(() => Sweeper(h, options: new CouncilSweepOptions { BatchSize = 0 }));
    }

    [Fact]
    public async Task The_worker_runs_a_pass_when_it_starts()
    {
        var h = new CouncilHarness();
        h.AddAgents("ada", "cal", "dot", "eve", "fin");
        var id = await h.CreateService().OpenAsync("ada", h.Motion(), CancellationToken.None);
        h.Clock.Now = h.Clock.Now.AddHours(73);
        var options = new CouncilSweepOptions { Interval = TimeSpan.FromHours(1) };
        var worker = new CouncilDeadlineWorker(Sweeper(h, options: options), options, h.Clock, NullLogger<CouncilDeadlineWorker>.Instance);

        await worker.StartAsync(CancellationToken.None);
        try
        {
            for (var attempt = 0; attempt < 200 && (await VoteOf(h, id)).Extensions == 0; attempt++)
            {
                await Task.Delay(25, CancellationToken.None);
            }

            Assert.Equal(1, (await VoteOf(h, id)).Extensions);
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
    }

    /// <summary>Delegates to a real service, except that the deadline step throws for one vote.</summary>
    private sealed class FailingCouncil(ICouncilService inner, Guid failingVoteId) : ICouncilService
    {
        public Task<Guid> OpenAsync(string callerId, MotionDraft motion, CancellationToken ct) =>
            inner.OpenAsync(callerId, motion, ct);

        public Task SubmitBallotAsync(Guid voteId, string voterId, Ballot ballot, CancellationToken ct) =>
            inner.SubmitBallotAsync(voteId, voterId, ballot, ct);

        public Task ProcessDeadlineAsync(Guid voteId, CancellationToken ct) =>
            voteId == failingVoteId
                ? throw new IOException("Simulated store failure.")
                : inner.ProcessDeadlineAsync(voteId, ct);

        public Task RedriveDecisionAsync(Guid voteId, CancellationToken ct) => inner.RedriveDecisionAsync(voteId, ct);

        public Task ResolveEscalationAsync(Guid voteId, string optionChoice, string? reasoning, CancellationToken ct) =>
            inner.ResolveEscalationAsync(voteId, optionChoice, reasoning, ct);

        public Task WithdrawAsync(Guid voteId, string actorId, CancellationToken ct) =>
            inner.WithdrawAsync(voteId, actorId, ct);
    }
}
