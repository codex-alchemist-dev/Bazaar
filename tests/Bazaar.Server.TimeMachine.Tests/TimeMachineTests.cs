using System.Runtime.CompilerServices;
using Bazaar.Contracts.Gc;
using Bazaar.Server.Gc;
using Bazaar.Server.Gc.Read;

namespace Bazaar.Server.TimeMachine.Tests;

internal sealed class FakeLog : IGcLogReader, IGcReadAccess
{
    public Guid GcId { get; } = Guid.NewGuid();

    public List<GcEvent> Events { get; } = [];

    /// <summary>Per-viewer grants; a viewer not listed is denied.</summary>
    public Dictionary<Guid, GcReadGrant> Grants { get; } = [];

    public int Appends { get; private set; }

    public ValueTask<GcReadGrant> GetGrantAsync(Guid gcId, GcPrincipal viewer, CancellationToken ct) =>
        ValueTask.FromResult(gcId == GcId && Grants.TryGetValue(viewer.Id, out var g) ? g : GcReadGrant.Denied);

    public Task<long> GetHeadSeqAsync(Guid gcId, CancellationToken ct) => Task.FromResult(Events.Count == 0 ? 0L : Events[^1].Seq);

    public async IAsyncEnumerable<GcEvent> ReadAsync(
        Guid gcId, long afterSeq, int limit, GcPrincipal viewer, [EnumeratorCancellation] CancellationToken ct)
    {
        var grant = await GetGrantAsync(gcId, viewer, ct);
        if (!grant.Allowed)
            throw new GcAccessDeniedException(gcId);
        foreach (var e in Events.Where(e => e.Seq > afterSeq && (grant.ThroughSeq is null || e.Seq <= grant.ThroughSeq)).Take(limit))
            yield return e;
    }

    public GcEvent Message(Guid messageId, int version, string text, Guid? author = null)
    {
        var e = new GcEvent(
            GcId, Events.Count + 1L, Guid.NewGuid(), version == 1 ? GcEventKinds.Message : GcEventKinds.MessageEdit,
            AuthorKind.Human, author ?? Guid.NewGuid(), SourceTrust.MemberHuman, false, null, messageId, version, null, null,
            "{\"text\":\"" + text + "\"}", DateTimeOffset.UnixEpoch.AddMinutes(Events.Count));
        Events.Add(e);
        return e;
    }

    public GcEvent Quarantine(long target)
    {
        var e = new GcEvent(
            GcId, Events.Count + 1L, Guid.NewGuid(), GcEventKinds.Quarantine, AuthorKind.System, Guid.NewGuid(),
            SourceTrust.System, false, null, null, null, target, null, null, DateTimeOffset.UnixEpoch);
        Events.Add(e);
        return e;
    }
}

public class ReplayProjectorTests
{
    private static readonly GcPrincipal _owner = new(Guid.NewGuid(), PrincipalKind.Owner, "Owner");
    private static readonly GcPrincipal _outsider = new(Guid.NewGuid(), PrincipalKind.Human, "Outsider");

    private static FakeLog Log()
    {
        var log = new FakeLog();
        log.Grants[_owner.Id] = GcReadGrant.Full;
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        log.Message(a, 1, "a1");
        log.Message(b, 1, "b1");
        log.Message(a, 2, "a2");
        log.Message(b, 2, "b2");
        log.Message(Guid.NewGuid(), 1, "c1");
        return log;
    }

    [Fact]
    public async Task Rebuild_from_zero_equals_live_state_at_N()
    {
        var log = Log();
        var projector = new ReplayProjector(log, log);

        var frame = await projector.ProjectAsync(new ReplayCursor(log.GcId, 3), _owner, CancellationToken.None);

        Assert.Equal(3, frame.EffectiveSeq);
        Assert.False(frame.Clamped);
        // Live state at 3: a at v2, b at v1, and the later edit and message are absent.
        Assert.Equal(2, frame.Messages.Count);
        Assert.Contains("a2", frame.Messages[0].PayloadJson);
        Assert.Equal(2, frame.Messages[0].Version);
        Assert.True(frame.Messages[0].Edited);
        Assert.Contains("b1", frame.Messages[1].PayloadJson);

        // The full replay matches folding the whole log directly.
        var full = await projector.ProjectAsync(new ReplayCursor(log.GcId, 5), _owner, CancellationToken.None);
        var expected = log.Events.Where(e => e.MessageId is not null).GroupBy(e => e.MessageId)
            .Select(g => g.MaxBy(e => e.Version)!.PayloadJson).ToHashSet();
        Assert.Equal(expected, full.Messages.Select(m => m.PayloadJson).ToHashSet());
    }

    [Fact]
    public async Task Dm_is_hidden_from_non_members()
    {
        var log = Log();
        var projector = new ReplayProjector(log, log);

        await Assert.ThrowsAsync<GcAccessDeniedException>(() =>
            projector.ProjectAsync(new ReplayCursor(log.GcId, 5), _outsider, CancellationToken.None));
    }

    [Fact]
    public async Task Seq_above_the_grant_is_clamped_to_the_grant()
    {
        var log = Log();
        var left = new GcPrincipal(Guid.NewGuid(), PrincipalKind.Human, "Left");
        log.Grants[left.Id] = GcReadGrant.Through(2);
        var projector = new ReplayProjector(log, log);

        var frame = await projector.ProjectAsync(new ReplayCursor(log.GcId, 5), left, CancellationToken.None);

        Assert.Equal(2, frame.EffectiveSeq);
        Assert.True(frame.Clamped);
        Assert.Equal(2, frame.Messages.Count);
        Assert.All(frame.Messages, m => Assert.Equal(1, m.Version));
    }

    [Fact]
    public async Task Missing_sources_degrade_to_unavailable()
    {
        var log = Log();
        var projector = new ReplayProjector(log, log, kanban: new ThrowingKanban(), souls: new FixedSouls());

        var frame = await projector.ProjectAsync(new ReplayCursor(log.GcId, 5), _owner, CancellationToken.None);

        Assert.False(frame.Kanban.IsAvailable);
        Assert.False(frame.Vault.IsAvailable);
        Assert.False(frame.Reputation.IsAvailable);
        Assert.True(frame.Souls.IsAvailable);
        Assert.Single(frame.Souls.Value!);
        Assert.Equal(3, frame.Messages.Count);
    }

    [Fact]
    public async Task Quarantined_version_is_dropped_for_agents_and_flagged_for_others()
    {
        var log = Log();
        var agent = new GcPrincipal(Guid.NewGuid(), PrincipalKind.Agent, "Agent");
        log.Grants[agent.Id] = GcReadGrant.Full;
        log.Quarantine(5);
        var projector = new ReplayProjector(log, log);

        var asAgent = await projector.ProjectAsync(new ReplayCursor(log.GcId, 6), agent, CancellationToken.None);
        var asOwner = await projector.ProjectAsync(new ReplayCursor(log.GcId, 6), _owner, CancellationToken.None);

        Assert.Equal(2, asAgent.Messages.Count);
        Assert.Equal(3, asOwner.Messages.Count);
        Assert.Single(asOwner.Messages, m => m.Quarantined);
    }

    private sealed class ThrowingKanban : IKanbanReplaySource
    {
        public Task<IReadOnlyList<ReplayTask>> GetAsync(Guid gcId, long seq, GcPrincipal viewer, CancellationToken ct) =>
            throw new InvalidOperationException("boom");
    }

    private sealed class FixedSouls : ISoulReplaySource
    {
        public Task<IReadOnlyList<ReplaySoulVersion>> GetAsync(Guid gcId, long seq, GcPrincipal viewer, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<ReplaySoulVersion>>([new ReplaySoulVersion(Guid.NewGuid(), 3)]);
    }
}

public class ForkServiceTests
{
    private static readonly GcPrincipal _owner = new(Guid.NewGuid(), PrincipalKind.Owner, "Owner");

    private sealed class CountingProvisioner : IForkTargetProvisioner
    {
        public int Calls { get; private set; }

        public Task<ForkTarget> ProvisionAsync(Guid sourceGcId, long effectiveSeq, GcPrincipal requester, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(new ForkTarget(Guid.NewGuid(), "abc123"));
        }
    }

    private static (ForkService Service, FakeLog Log, InMemoryForkLinkStore Store, CountingProvisioner Provisioner) Make()
    {
        var log = new FakeLog();
        log.Grants[_owner.Id] = GcReadGrant.Full;
        for (var i = 0; i < 5; i++)
            log.Message(Guid.NewGuid(), 1, "m" + i);
        var store = new InMemoryForkLinkStore();
        var provisioner = new CountingProvisioner();
        return (new ForkService(log, log, store, provisioner), log, store, provisioner);
    }

    [Fact]
    public async Task Fork_writes_no_messages_and_one_link()
    {
        var (service, log, store, _) = Make();
        var before = log.Events.Count;

        var result = await service.ForkAsync(new ForkRequest(Guid.NewGuid(), log.GcId, 3, _owner), CancellationToken.None);

        Assert.True(result.Created);
        Assert.Equal(before, log.Events.Count);
        Assert.Equal(3, result.Link.SourceSeq);
        Assert.Equal("abc123", result.Link.VaultCommit);
        Assert.Single(await store.ListBySourceAsync(log.GcId, CancellationToken.None));
    }

    [Fact]
    public async Task Fork_is_idempotent_on_source_seq_and_request_id()
    {
        var (service, log, store, provisioner) = Make();
        var request = new ForkRequest(Guid.NewGuid(), log.GcId, 3, _owner);

        var first = await service.ForkAsync(request, CancellationToken.None);
        var second = await service.ForkAsync(request, CancellationToken.None);
        var other = await service.ForkAsync(request with { RequestId = Guid.NewGuid() }, CancellationToken.None);

        Assert.True(first.Created);
        Assert.False(second.Created);
        Assert.Equal(first.Link.NewGcId, second.Link.NewGcId);
        Assert.NotEqual(first.Link.NewGcId, other.Link.NewGcId);
        Assert.Equal(2, provisioner.Calls);
        Assert.Equal(2, (await store.ListBySourceAsync(log.GcId, CancellationToken.None)).Count);
    }

    [Fact]
    public async Task Fork_clamps_seq_to_the_grant_and_refuses_non_members()
    {
        var (service, log, _, provisioner) = Make();
        var left = new GcPrincipal(Guid.NewGuid(), PrincipalKind.Human, "Left");
        log.Grants[left.Id] = GcReadGrant.Through(2);
        var stranger = new GcPrincipal(Guid.NewGuid(), PrincipalKind.Human, "Stranger");

        var result = await service.ForkAsync(new ForkRequest(Guid.NewGuid(), log.GcId, 99, left), CancellationToken.None);

        Assert.Equal(2, result.Link.SourceSeq);
        await Assert.ThrowsAsync<GcAccessDeniedException>(() =>
            service.ForkAsync(new ForkRequest(Guid.NewGuid(), log.GcId, 1, stranger), CancellationToken.None));
        Assert.Equal(1, provisioner.Calls);
    }
}
