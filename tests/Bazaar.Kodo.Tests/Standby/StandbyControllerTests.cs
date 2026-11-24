using Bazaar.Kodo.Standby;
using Microsoft.Extensions.Time.Testing;

namespace Bazaar.Kodo.Tests.Standby;

public sealed class StandbyControllerTests
{
    private sealed class Witness(string id) : IStandbyWitness
    {
        public LeaseRecord? Lease { get; set; }

        public long HighestGranted { get; set; }

        public WitnessAnswer Answer { get; set; } = WitnessAnswer.PrimaryUnreachable;

        public bool Down { get; set; }

        public bool Hang { get; set; }

        public string Id { get; } = id;

        public async ValueTask<WitnessReport> ObserveAsync(string primaryId, CancellationToken cancellationToken)
        {
            await Gate(cancellationToken);
            return new WitnessReport(Id, Answer, Lease);
        }

        public async ValueTask<GrantResponse> RequestGrantAsync(LeaseRecord lease, CancellationToken cancellationToken)
        {
            await Gate(cancellationToken);
            if (lease.Epoch < HighestGranted || (Lease is not null && lease.Epoch == Lease.Epoch && lease.HolderId != Lease.HolderId))
            {
                return new GrantResponse(false, HighestGranted);
            }

            HighestGranted = lease.Epoch;
            Lease = lease;
            return new GrantResponse(true, HighestGranted);
        }

        private async ValueTask Gate(CancellationToken token)
        {
            if (Down)
            {
                throw new IOException("down");
            }

            if (Hang)
            {
                await Task.Delay(Timeout.Infinite, token);
            }
        }
    }

    private sealed class Journal : IStandbyJournal
    {
        public List<(string Kind, byte[] Payload)> Entries { get; } = [];

        public bool Fail { get; set; }

        public ValueTask AppendAsync(DateTimeOffset at, string kind, byte[] payload, CancellationToken cancellationToken)
        {
            if (Fail)
            {
                throw new IOException("journal");
            }

            Entries.Add((kind, payload));
            return ValueTask.CompletedTask;
        }
    }

    private sealed class Store : IStandbyStateStore
    {
        public StandbyState? Saved { get; set; }

        public bool FailSave { get; set; }

        public bool FailLoad { get; set; }

        public ValueTask<StandbyState?> LoadAsync(CancellationToken cancellationToken) =>
            FailLoad ? throw new IOException("load") : new ValueTask<StandbyState?>(Saved);

        public ValueTask SaveAsync(StandbyState state, CancellationToken cancellationToken)
        {
            if (FailSave)
            {
                throw new IOException("save");
            }

            Saved = state;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class Rig
    {
        public Rig(string node = "b", string peer = "a")
        {
            Time = new FakeTimeProvider(new DateTimeOffset(2026, 11, 22, 8, 0, 0, TimeSpan.Zero));
            Witnesses = [new("w1"), new("w2"), new("w3")];
            Controller = new StandbyController(node, peer, new WitnessQuorumPolicy(Witnesses.Select(w => w.Id)), Witnesses, Journal, Store, new StandbyOptions(), Time);
        }

        public FakeTimeProvider Time { get; }

        public Witness[] Witnesses { get; }

        public Journal Journal { get; } = new();

        public Store Store { get; } = new();

        public StandbyController Controller { get; }

        public void GiveLease(string holder, long epoch, double seconds = 15)
        {
            LeaseRecord lease = LeaseRecord.Create(holder, new FencingToken(epoch), Time.GetUtcNow(), TimeSpan.FromSeconds(seconds));
            foreach (Witness w in Witnesses)
            {
                w.Lease = lease;
                w.HighestGranted = epoch;
            }
        }

        public Task<StandbyTickResult> Tick() => Controller.TickAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task StandbyLearnsLeaseAndWaitsForExpiryAndMargin()
    {
        Rig rig = new();
        rig.GiveLease("a", 1);
        Assert.Equal(FailoverReasons.LeaseNotExpired, (await rig.Tick()).Outcome);
        rig.Time.Advance(TimeSpan.FromSeconds(16));
        Assert.Equal(FailoverReasons.LeaseNotExpired, (await rig.Tick()).Outcome);
        rig.Time.Advance(TimeSpan.FromSeconds(3));
        StandbyTickResult result = await rig.Tick();
        Assert.Equal(StandbyOutcomes.Promoted, result.Outcome);
        Assert.Equal(StandbyRole.Primary, result.Role);
        Assert.Equal(2, result.Epoch);
        Assert.Equal(StandbyJournalKinds.Promoted, Assert.Single(rig.Journal.Entries).Kind);
        Assert.Equal(2, rig.Store.Saved!.Epoch);
    }

    [Fact]
    public async Task NeverPromotesWithoutQuorum()
    {
        Rig rig = new();
        rig.GiveLease("a", 1);
        rig.Witnesses[1].Down = true;
        rig.Witnesses[2].Down = true;
        rig.Time.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(FailoverReasons.NoQuorum, (await rig.Tick()).Outcome);
        Assert.Equal(StandbyRole.Standby, rig.Controller.Current.Role);
        Assert.Empty(rig.Journal.Entries);
    }

    [Fact]
    public async Task NeverPromotesWhenAWitnessStillSeesThePrimary()
    {
        Rig rig = new();
        rig.GiveLease("a", 1);
        rig.Witnesses[0].Answer = WitnessAnswer.PrimaryReachable;
        rig.Time.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(FailoverReasons.PrimaryCanRenew, (await rig.Tick()).Outcome);
    }

    [Fact]
    public async Task HangingWitnessCountsAsNoAnswer()
    {
        Rig rig = new();
        rig.GiveLease("a", 1);
        rig.Witnesses[2].Hang = true;
        rig.Time.Advance(TimeSpan.FromMinutes(1));
        Task<StandbyTickResult> pending = rig.Tick();
        for (int i = 0; i < 200 && !pending.IsCompleted; i++)
        {
            rig.Time.Advance(TimeSpan.FromSeconds(4));
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        Assert.Equal(StandbyOutcomes.Promoted, (await pending).Outcome);
    }

    [Fact]
    public async Task WitnessGrantQuorumMissingKeepsStandby()
    {
        Rig rig = new();
        rig.GiveLease("a", 1);
        rig.Time.Advance(TimeSpan.FromMinutes(1));
        rig.Witnesses[0].HighestGranted = 50;
        rig.Witnesses[1].HighestGranted = 50;
        StandbyTickResult result = await rig.Tick();
        Assert.Equal(StandbyRole.Standby, result.Role);
        Assert.Equal(StandbyOutcomes.GrantQuorumMissing, result.Outcome);
        Assert.Equal(50, rig.Controller.Current.HighestEpochSeen);
    }

    [Fact]
    public async Task JournalFailureRefusesPromotion()
    {
        Rig rig = new();
        rig.GiveLease("a", 1);
        rig.Time.Advance(TimeSpan.FromMinutes(1));
        rig.Journal.Fail = true;
        Assert.Equal(StandbyOutcomes.JournalUnavailable, (await rig.Tick()).Outcome);
        Assert.Equal(StandbyRole.Standby, rig.Controller.Current.Role);
    }

    [Fact]
    public async Task StoreFailureRefusesPromotionAndJournalsAbort()
    {
        Rig rig = new();
        rig.GiveLease("a", 1);
        rig.Time.Advance(TimeSpan.FromMinutes(1));
        rig.Store.FailSave = true;
        Assert.Equal(StandbyOutcomes.StoreUnavailable, (await rig.Tick()).Outcome);
        Assert.Equal(StandbyRole.Standby, rig.Controller.Current.Role);
        Assert.Contains(rig.Journal.Entries, e => e.Kind == StandbyJournalKinds.PromotionAborted);
    }

    private static async Task<Rig> PrimaryRig()
    {
        Rig rig = new("a", "b");
        StandbyTickResult boot = await rig.Controller.BootstrapAsync(TestContext.Current.CancellationToken);
        Assert.Equal(StandbyOutcomes.Promoted, boot.Outcome);
        Assert.Equal(1, boot.Epoch);
        return rig;
    }

    [Fact]
    public async Task BootstrapRefusedWhenALeaseExists()
    {
        Rig rig = new("a", "b");
        rig.GiveLease("b", 1);
        Assert.Equal(StandbyOutcomes.NotBootstrappable, (await rig.Controller.BootstrapAsync(TestContext.Current.CancellationToken)).Outcome);
    }

    [Fact]
    public async Task PrimaryRenewsInsideTheRenewWindowOnly()
    {
        Rig rig = await PrimaryRig();
        Assert.Equal(StandbyOutcomes.Idle, (await rig.Tick()).Outcome);
        rig.Time.Advance(TimeSpan.FromSeconds(11));
        Assert.Equal(StandbyOutcomes.Renewed, (await rig.Tick()).Outcome);
        Assert.True(rig.Controller.Current.Lease!.ExpiresAt > rig.Time.GetUtcNow().AddSeconds(14));
    }

    [Fact]
    public async Task PrimaryThatCannotRenewFencesAtExpiry()
    {
        Rig rig = await PrimaryRig();
        rig.Witnesses[0].Down = true;
        rig.Witnesses[1].Down = true;
        rig.Time.Advance(TimeSpan.FromSeconds(11));
        Assert.Equal(StandbyOutcomes.RenewFailed, (await rig.Tick()).Outcome);
        rig.Time.Advance(TimeSpan.FromSeconds(5));
        StandbyTickResult result = await rig.Tick();
        Assert.Equal(StandbyRole.Fenced, result.Role);
        Assert.Equal(StandbyCauses.LeaseLost, result.Cause);
        Assert.Contains(rig.Journal.Entries, e => e.Kind == StandbyJournalKinds.Fenced);
    }

    [Fact]
    public async Task FencingSucceedsEvenWhenJournalAndStoreFail()
    {
        Rig rig = await PrimaryRig();
        rig.Journal.Fail = true;
        rig.Store.FailSave = true;
        rig.Time.Advance(TimeSpan.FromSeconds(20));
        Assert.Equal(StandbyRole.Fenced, (await rig.Tick()).Role);
    }

    [Fact]
    public async Task PrimaryFencesWhenWitnessesShowNewerEpoch()
    {
        Rig rig = await PrimaryRig();
        foreach (Witness w in rig.Witnesses)
        {
            w.HighestGranted = 5;
        }

        rig.Time.Advance(TimeSpan.FromSeconds(11));
        StandbyTickResult result = await rig.Tick();
        Assert.Equal(StandbyCauses.Superseded, result.Cause);
    }

    [Fact]
    public async Task AdmitRejectsStaleAndFencesOnAhead()
    {
        Rig rig = await PrimaryRig();
        CancellationToken ct = TestContext.Current.CancellationToken;
        Assert.Equal(FencingVerdict.Current, await rig.Controller.AdmitAsync(new FencingToken(1), ct));
        Assert.Equal(FencingVerdict.Stale, await rig.Controller.AdmitAsync(new FencingToken(0), ct));
        Assert.Contains(rig.Journal.Entries, e => e.Kind == StandbyJournalKinds.StaleRejected);
        Assert.Equal(FencingVerdict.Ahead, await rig.Controller.AdmitAsync(new FencingToken(2), ct));
        Assert.Equal(StandbyRole.Fenced, rig.Controller.Current.Role);
        Assert.Equal(FencingVerdict.NotPrimary, await rig.Controller.AdmitAsync(new FencingToken(1), ct));
    }

    [Fact]
    public async Task AdmitRefusesAfterLeaseExpiry()
    {
        Rig rig = await PrimaryRig();
        rig.Time.Advance(TimeSpan.FromSeconds(16));
        Assert.Equal(FencingVerdict.NotPrimary, await rig.Controller.AdmitAsync(new FencingToken(1), TestContext.Current.CancellationToken));
        Assert.Equal(StandbyRole.Fenced, rig.Controller.Current.Role);
    }

    [Fact]
    public async Task EqualPeerEpochIsSplitBrainAndNeedsAHumanToRejoin()
    {
        Rig rig = await PrimaryRig();
        CancellationToken ct = TestContext.Current.CancellationToken;
        StandbyTickResult result = await rig.Controller.ReportPeerClaimAsync(1, ct);
        Assert.True(result.SplitBrain);
        Assert.False(await rig.Controller.RejoinAsStandbyAsync(false, ct));
        Assert.True(await rig.Controller.RejoinAsStandbyAsync(true, ct));
        Assert.Equal(StandbyRole.Standby, rig.Controller.Current.Role);
    }

    [Fact]
    public async Task OlderPeerClaimIsIgnoredNewerSupersedes()
    {
        Rig rig = await PrimaryRig();
        CancellationToken ct = TestContext.Current.CancellationToken;
        Assert.Equal(StandbyRole.Primary, (await rig.Controller.ReportPeerClaimAsync(0, ct)).Role);
        StandbyTickResult result = await rig.Controller.ReportPeerClaimAsync(3, ct);
        Assert.Equal(StandbyCauses.Superseded, result.Cause);
        Assert.False(result.SplitBrain);
        Assert.True(await rig.Controller.RejoinAsStandbyAsync(false, ct));
    }

    [Fact]
    public async Task RejoinNeedsJournal()
    {
        Rig rig = await PrimaryRig();
        CancellationToken ct = TestContext.Current.CancellationToken;
        await rig.Controller.ReportPeerClaimAsync(3, ct);
        rig.Journal.Fail = true;
        Assert.False(await rig.Controller.RejoinAsStandbyAsync(false, ct));
        Assert.Equal(StandbyRole.Fenced, rig.Controller.Current.Role);
    }

    [Fact]
    public async Task FencedNodeWaitsForAPerson()
    {
        Rig rig = await PrimaryRig();
        await rig.Controller.ReportPeerClaimAsync(3, TestContext.Current.CancellationToken);
        Assert.Equal(StandbyOutcomes.WaitingForPerson, (await rig.Tick()).Outcome);
    }

    [Fact]
    public async Task UnreadableStateFences()
    {
        Rig rig = new();
        rig.Store.FailLoad = true;
        StandbyTickResult result = await rig.Controller.InitializeAsync(TestContext.Current.CancellationToken);
        Assert.Equal(StandbyCauses.StateUnreadable, result.Cause);
    }

    [Fact]
    public async Task RestartedPrimaryWithExpiredLeaseFences()
    {
        Rig first = await PrimaryRig();
        Rig second = new("a", "b");
        second.Store.Saved = first.Store.Saved;
        second.Time.Advance(TimeSpan.FromMinutes(1));
        StandbyTickResult result = await second.Controller.InitializeAsync(TestContext.Current.CancellationToken);
        Assert.Equal(StandbyCauses.RestartLeaseLost, result.Cause);
    }

    [Fact]
    public async Task RestartedPrimaryWithLiveLeaseResumes()
    {
        Rig first = await PrimaryRig();
        Rig second = new("a", "b");
        second.Store.Saved = first.Store.Saved;
        StandbyTickResult result = await second.Controller.InitializeAsync(TestContext.Current.CancellationToken);
        Assert.Equal(StandbyRole.Primary, result.Role);
    }

    [Fact]
    public async Task StateForAnotherNodeFences()
    {
        Rig first = await PrimaryRig();
        Rig second = new("c", "a");
        second.Store.Saved = first.Store.Saved;
        Assert.Equal(StandbyCauses.StateUnreadable, (await second.Controller.InitializeAsync(TestContext.Current.CancellationToken)).Cause);
    }

    [Fact]
    public async Task OnlyOneOfTwoCompetingStandbysCanWin()
    {
        FakeTimeProvider time = new(new DateTimeOffset(2026, 11, 22, 8, 0, 0, TimeSpan.Zero));
        Witness[] ws = [new("w1"), new("w2"), new("w3")];
        LeaseRecord old = LeaseRecord.Create("a", new FencingToken(1), time.GetUtcNow(), TimeSpan.FromSeconds(15));
        foreach (Witness w in ws)
        {
            w.Lease = old;
            w.HighestGranted = 1;
        }

        StandbyController Make(string id) => new(id, "a", new WitnessQuorumPolicy(ws.Select(w => w.Id)), ws, new Journal(), new Store(), new StandbyOptions(), time);
        StandbyController b = Make("b");
        StandbyController c = Make("c");
        time.Advance(TimeSpan.FromMinutes(1));
        CancellationToken ct = TestContext.Current.CancellationToken;
        StandbyTickResult rb = await b.TickAsync(ct);
        StandbyTickResult rc = await c.TickAsync(ct);
        Assert.Equal(StandbyOutcomes.Promoted, rb.Outcome);
        Assert.NotEqual(StandbyOutcomes.Promoted, rc.Outcome);
        Assert.Single(new[] { b, c }, x => x.Current.Role == StandbyRole.Primary);
        b.Dispose();
        c.Dispose();
    }

    [Fact]
    public void ConstructorChecksWitnessesAndOptions()
    {
        Rig rig = new();
        WitnessQuorumPolicy policy = new(["w1", "w2", "w3"]);
        Assert.Throws<ArgumentException>(() => new StandbyController("a", "b", policy, rig.Witnesses.Take(2), rig.Journal, rig.Store, new StandbyOptions(), rig.Time));
        Assert.Throws<ArgumentException>(() => new StandbyController("a", "b", policy, rig.Witnesses, rig.Journal, rig.Store, new StandbyOptions { RenewBefore = TimeSpan.FromMinutes(1) }, rig.Time));
    }
}
