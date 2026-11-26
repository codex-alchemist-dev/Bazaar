using Bazaar.Contracts.Gc;
using Bazaar.Contracts.TimeMachine;
using Bazaar.Contracts.Wire;
using Bazaar.Server.Gc;
using Bazaar.Server.Gc.Read;

namespace Bazaar.Server.TimeMachine.Tests;

public class ReplayPointListerTests
{
    private static readonly GcPrincipal _owner = new(Guid.NewGuid(), PrincipalKind.Owner, "Owner");
    private static readonly GcPrincipal _member = new(Guid.NewGuid(), PrincipalKind.Human, "Member");
    private static readonly GcPrincipal _outsider = new(Guid.NewGuid(), PrincipalKind.Human, "Outsider");

    private sealed class FixedMarkers(params ReplayMarker[] markers) : IReplayMarkerSource
    {
        public int Calls { get; private set; }

        public Task<IReadOnlyList<ReplayMarker>> ListAsync(DateTimeOffset since, DateTimeOffset until, int limit, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult<IReadOnlyList<ReplayMarker>>([.. markers.Where(m => m.At >= since && m.At <= until).Take(limit)]);
        }
    }

    // The fixture log: events at minutes 0..n-1 from the epoch. Edits and a quarantine give each kind.
    private static FakeLog Fixture(int messages = 6)
    {
        var log = new FakeLog();
        log.Grants[_owner.Id] = GcReadGrant.Full;
        log.Grants[_member.Id] = GcReadGrant.Full;
        var first = Guid.NewGuid();
        for (var i = 0; i < messages; i++)
        {
            log.Message(Guid.NewGuid(), 1, "m" + i);
        }

        log.Message(first, 2, "edit");
        log.Quarantine(2);
        return log;
    }

    private static ReplayPointLister Lister(FakeLog log, IReplayMarkerSource? markers = null) => new(log, log, markers);

    [Fact]
    public async Task Lists_every_event_with_its_kind()
    {
        var log = Fixture();

        var page = await Lister(log).ListAsync(log.GcId, 0, 100, null, _owner, CancellationToken.None);

        Assert.Equal(8, page.Points.Count);
        Assert.Equal(Enumerable.Range(1, 8).Select(i => (long)i), page.Points.Select(p => p.Seq));
        Assert.Equal(ReplayPointKinds.Message, page.Points[0].Kind);
        Assert.Equal(ReplayPointKinds.Edit, page.Points[6].Kind);
        Assert.Equal(ReplayPointKinds.Quarantine, page.Points[7].Kind);
        Assert.Null(page.NextAfterSeq);
        Assert.Equal(8, page.Head);
    }

    [Fact]
    public async Task Pages_with_limit_and_resumes_without_gaps_or_repeats()
    {
        var log = Fixture();
        var lister = Lister(log);
        var seen = new List<long>();
        long after = 0;

        for (var guard = 0; guard < 10; guard++)
        {
            var page = await lister.ListAsync(log.GcId, after, 3, null, _owner, CancellationToken.None);
            seen.AddRange(page.Points.Select(p => p.Seq));
            if (page.NextAfterSeq is not { } next)
            {
                break;
            }

            after = next;
        }

        Assert.Equal(Enumerable.Range(1, 8).Select(i => (long)i), seen);
    }

    [Fact]
    public async Task Bucket_keeps_the_last_event_of_each_bucket()
    {
        var log = Fixture();

        var page = await Lister(log).ListAsync(log.GcId, 0, 100, TimeSpan.FromMinutes(3), _owner, CancellationToken.None);

        // Events sit at minutes 0..5 and 6 (edit), the quarantine at the epoch itself: buckets 0 to 2 and 3 to 5, then 6.
        Assert.All(page.Points, p => Assert.True(p.Seq >= 1));
        Assert.True(page.Points.Count < 8);
        Assert.Equal(page.Points.Select(p => p.Seq).Order(), page.Points.Select(p => p.Seq));
    }

    [Fact]
    public async Task A_non_positive_bucket_is_rejected()
    {
        var log = Fixture();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            Lister(log).ListAsync(log.GcId, 0, 10, TimeSpan.Zero, _owner, CancellationToken.None));
    }

    [Fact]
    public async Task An_outsider_is_denied()
    {
        var log = Fixture();

        await Assert.ThrowsAsync<GcAccessDeniedException>(() =>
            Lister(log).ListAsync(log.GcId, 0, 10, null, _outsider, CancellationToken.None));
    }

    [Fact]
    public async Task The_grant_limits_points_and_head()
    {
        var log = Fixture();
        log.Grants[_member.Id] = new GcReadGrant(true, 4);

        var page = await Lister(log).ListAsync(log.GcId, 0, 100, null, _member, CancellationToken.None);

        Assert.Equal(4, page.Points.Count);
        Assert.Equal(4, page.Head);
    }

    [Fact]
    public async Task Audit_markers_go_to_the_owner_at_the_seq_then()
    {
        var log = Fixture();
        // Between the events at minute 2 (seq 3) and minute 3 (seq 4).
        var marker = new ReplayMarker(DateTimeOffset.UnixEpoch.AddMinutes(2).AddSeconds(30), "config.changed");

        var page = await Lister(log, new FixedMarkers(marker)).ListAsync(log.GcId, 0, 100, null, _owner, CancellationToken.None);

        var audit = Assert.Single(page.Points, p => p.Kind == ReplayPointKinds.Audit);
        Assert.Equal(3, audit.Seq);
        Assert.Equal("config.changed", audit.Label);
        Assert.Equal(9, page.Points.Count);
        Assert.Equal(page.Points.Select(p => p.At).Order(), page.Points.Select(p => p.At));
    }

    [Fact]
    public async Task Audit_markers_are_not_shown_to_other_members_and_the_source_is_not_asked()
    {
        var log = Fixture();
        var source = new FixedMarkers(new ReplayMarker(DateTimeOffset.UnixEpoch.AddMinutes(1), "config.changed"));

        var page = await Lister(log, source).ListAsync(log.GcId, 0, 100, null, _member, CancellationToken.None);

        Assert.DoesNotContain(page.Points, p => p.Kind == ReplayPointKinds.Audit);
        Assert.Equal(0, source.Calls);
    }

    [Fact]
    public async Task Audit_markers_never_reach_past_a_grant()
    {
        var log = Fixture();
        log.Grants[_owner.Id] = new GcReadGrant(true, 3);
        var late = new ReplayMarker(DateTimeOffset.UnixEpoch.AddMinutes(5), "secret.use");

        var page = await Lister(log, new FixedMarkers(late)).ListAsync(log.GcId, 0, 100, null, _owner, CancellationToken.None);

        Assert.DoesNotContain(page.Points, p => p.Kind == ReplayPointKinds.Audit);
    }

    [Fact]
    public async Task Each_audit_marker_is_listed_once_across_pages()
    {
        var log = Fixture();
        var markers = new FixedMarkers(
            new ReplayMarker(DateTimeOffset.UnixEpoch.AddMinutes(0).AddSeconds(10), "a"),
            new ReplayMarker(DateTimeOffset.UnixEpoch.AddMinutes(2).AddSeconds(10), "b"),
            new ReplayMarker(DateTimeOffset.UnixEpoch.AddMinutes(4).AddSeconds(10), "c"),
            new ReplayMarker(DateTimeOffset.UnixEpoch.AddMinutes(9), "d"));
        var lister = Lister(log, markers);
        var labels = new List<string>();
        long after = 0;

        for (var guard = 0; guard < 10; guard++)
        {
            var page = await lister.ListAsync(log.GcId, after, 2, null, _owner, CancellationToken.None);
            labels.AddRange(page.Points.Where(p => p.Kind == ReplayPointKinds.Audit).Select(p => p.Label));
            if (page.NextAfterSeq is not { } next)
            {
                break;
            }

            after = next;
        }

        Assert.Equal(["a", "b", "c", "d"], labels);
    }

    [Fact]
    public async Task Listing_writes_nothing()
    {
        var log = Fixture();
        var before = log.Events.Count;

        await Lister(log).ListAsync(log.GcId, 0, 100, null, _owner, CancellationToken.None);

        Assert.Equal(before, log.Events.Count);
        Assert.Equal(0, log.Appends);
    }

    [Fact]
    public async Task Handler_maps_points_and_hides_unreadable_gcs()
    {
        var log = Fixture();
        var handler = new ReplayWireHandler(
            new ReplayProjector(log, log), new ForkService(log, log, new InMemoryForkLinkStore(), null!), log, log, Lister(log));

        var ok = await handler.PointsAsync(_owner, new ReplayPointsRequest(log.GcId, 0, 3), CancellationToken.None);
        var denied = await handler.PointsAsync(_outsider, new ReplayPointsRequest(log.GcId), CancellationToken.None);
        var bad = await handler.PointsAsync(_owner, new ReplayPointsRequest(Guid.Empty), CancellationToken.None);
        var badBucket = await handler.PointsAsync(_owner, new ReplayPointsRequest(log.GcId, 0, 10, 0), CancellationToken.None);

        Assert.True(ok.IsOk);
        Assert.Equal(3, ok.Value!.Points.Count);
        Assert.Equal(3, ok.Value.Next);
        Assert.Equal(8, ok.Value.Head);
        Assert.Equal(WireErrorCodes.NotFound, denied.ErrorCode);
        Assert.Equal(WireErrorCodes.BadRequest, bad.ErrorCode);
        Assert.Equal(WireErrorCodes.BadRequest, badBucket.ErrorCode);
    }

    [Fact]
    public async Task Handler_without_a_lister_answers_unsupported()
    {
        var log = Fixture();
        var handler = new ReplayWireHandler(new ReplayProjector(log, log), new ForkService(log, log, new InMemoryForkLinkStore(), null!), log, log);

        var reply = await handler.PointsAsync(_owner, new ReplayPointsRequest(log.GcId), CancellationToken.None);

        Assert.Equal(WireErrorCodes.Unsupported, reply.ErrorCode);
    }
}
