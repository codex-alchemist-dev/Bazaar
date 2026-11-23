using Bazaar.Server.Council.Escalation;
using Bazaar.Server.Council.Service;
using Bazaar.Server.Council.Tests.Service;
using Microsoft.Extensions.Logging.Abstractions;

namespace Bazaar.Server.Council.Tests.Escalation;

public sealed class CouncilEscalationTests
{
    private static readonly CancellationToken _ct = CancellationToken.None;

    private sealed class FakeSource : ICouncilEscalationSource
    {
        public List<Guid> Ids { get; } = [];

        public Task<IReadOnlyList<Guid>> ListEscalatedAsync(int limit, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<Guid>>([.. Ids.Take(limit)]);
    }

    private sealed class RecordingNotifier : ICouncilOwnerNotifier
    {
        public List<Guid> Sent { get; } = [];

        public Task NotifyEscalationAsync(EscalatedVote vote, CancellationToken ct)
        {
            Sent.Add(vote.VoteId);
            return Task.CompletedTask;
        }
    }

    private sealed class FixedHours(int hours) : ICouncilEscalationRenotifySource
    {
        public Task<int> GetRenotifyHoursAsync(CancellationToken ct) => Task.FromResult(hours);
    }

    // Four voters split 2-2 twice: follow-up tie, then Escalated, after a revealed independent round.
    private static async Task<Guid> EscalateTieAsync(CouncilHarness h)
    {
        h.AddAgents("ada", "cal", "dot", "eve", "fin");
        var service = h.CreateService();
        var id = await service.OpenAsync("ada", h.Motion(), _ct);
        foreach (var (voter, choice) in new[] { ("cal", "yes"), ("dot", "yes"), ("eve", "no"), ("fin", "no") })
        {
            await service.SubmitBallotAsync(id, voter, Ballots.Independent(voter, choice), _ct);
        }

        foreach (var (voter, choice) in new[] { ("cal", "yes"), ("dot", "yes"), ("eve", "no"), ("fin", "no") })
        {
            await service.SubmitBallotAsync(id, voter, Ballots.FollowUp(voter, choice), _ct);
        }

        return id;
    }

    private static (CouncilEscalationService Service, FakeSource Queue) Create(CouncilHarness h)
    {
        var queue = new FakeSource();
        return (new CouncilEscalationService(queue, h.Store, h.CreateService()), queue);
    }

    [Fact]
    public async Task Lists_an_escalated_vote_with_options_revealed_ballots_and_age_anchor()
    {
        var h = new CouncilHarness();
        var id = await EscalateTieAsync(h);
        var (service, queue) = Create(h);
        queue.Ids.Add(id);

        var listed = Assert.Single(await service.ListAwaitingAsync(_ct));

        Assert.Equal(id, listed.VoteId);
        Assert.Equal("task/42", listed.SubjectRef);
        Assert.Equal(["yes", "no"], listed.Options);
        Assert.True(listed.BallotsRevealed);
        Assert.Equal(8, listed.Ballots.Count);
        Assert.Equal(4, listed.IndependentBallotCount);
        Assert.Equal(h.Clock.Now, listed.EscalatedAt);
        Assert.Contains("tie", listed.EscalationReason);
    }

    [Fact]
    public async Task A_no_quorum_escalation_shows_no_sealed_ballots()
    {
        var h = new CouncilHarness();
        h.AddAgents("ada", "cal", "dot", "eve", "fin");
        var service = h.CreateService();
        var id = await service.OpenAsync("ada", h.Motion(), _ct);
        await service.SubmitBallotAsync(id, "cal", Ballots.Independent("cal", "yes"), _ct);
        h.Clock.Now = (await h.Store.FindVoteAsync(id, _ct))!.DeadlineAt.AddMinutes(1);
        await service.ProcessDeadlineAsync(id, _ct);
        h.Clock.Now = (await h.Store.FindVoteAsync(id, _ct))!.DeadlineAt.AddMinutes(1);
        await service.ProcessDeadlineAsync(id, _ct);
        var (escalations, queue) = Create(h);
        queue.Ids.Add(id);

        var view = Assert.Single(await escalations.ListAwaitingAsync(_ct));

        Assert.False(view.BallotsRevealed);
        Assert.Empty(view.Ballots);
        Assert.Equal(1, view.IndependentBallotCount);
    }

    [Fact]
    public async Task Resolving_decides_the_vote_and_it_leaves_the_list()
    {
        var h = new CouncilHarness();
        var id = await EscalateTieAsync(h);
        var (service, queue) = Create(h);
        queue.Ids.Add(id);

        await service.ResolveAsync(id, "no", "The owner declines.", _ct);

        Assert.Equal(VoteState.Decided, (await h.Store.FindVoteAsync(id, _ct))!.State);
        Assert.Empty(await service.ListAwaitingAsync(_ct));
        Assert.Null(await service.ViewAsync(id, _ct));
    }

    [Fact]
    public async Task Resolving_with_an_unlisted_choice_is_rejected_and_leaves_the_vote_escalated()
    {
        var h = new CouncilHarness();
        var id = await EscalateTieAsync(h);
        var (service, _) = Create(h);

        await Assert.ThrowsAsync<CouncilRejectedException>(() => service.ResolveAsync(id, "maybe", null, _ct));

        Assert.Equal(VoteState.Escalated, (await h.Store.FindVoteAsync(id, _ct))!.State);
    }

    [Fact]
    public async Task Renotifier_alerts_at_once_then_once_per_interval_and_stops_after_resolution()
    {
        var h = new CouncilHarness();
        var id = await EscalateTieAsync(h);
        var (service, queue) = Create(h);
        queue.Ids.Add(id);
        var notifier = new RecordingNotifier();
        var renotifier = new CouncilEscalationRenotifier(
            service, h.Clock, new CouncilEscalationOptions(), NullLogger<CouncilEscalationRenotifier>.Instance, notifier, new FixedHours(4));

        Assert.Equal(1, await renotifier.RunOnceAsync(_ct));
        h.Clock.Now = h.Clock.Now.AddHours(3);
        Assert.Equal(0, await renotifier.RunOnceAsync(_ct));
        h.Clock.Now = h.Clock.Now.AddHours(1);
        Assert.Equal(1, await renotifier.RunOnceAsync(_ct));
        Assert.Equal([id, id], notifier.Sent);

        await service.ResolveAsync(id, "yes", null, _ct);
        h.Clock.Now = h.Clock.Now.AddHours(10);
        Assert.Equal(0, await renotifier.RunOnceAsync(_ct));
    }

    [Fact]
    public async Task Renotifier_without_a_notifier_does_nothing()
    {
        var h = new CouncilHarness();
        var id = await EscalateTieAsync(h);
        var (service, queue) = Create(h);
        queue.Ids.Add(id);
        var renotifier = new CouncilEscalationRenotifier(
            service, h.Clock, new CouncilEscalationOptions(), NullLogger<CouncilEscalationRenotifier>.Instance);

        Assert.Equal(0, await renotifier.RunOnceAsync(_ct));
    }
}
