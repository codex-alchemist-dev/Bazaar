using Bazaar.Contracts.Kodo;
using Bazaar.Kodo.Api;
using Bazaar.Kodo.Evaluator;

namespace Bazaar.Kodo.Tests.Api;

public sealed class KodoStreamWriterTests
{
    private static readonly DateTimeOffset _now = KodoApiTestData.Now;

    [Fact]
    public void FirstEventOfANewStreamIsRevisionOne()
    {
        KodoStreamWriter writer = new();

        Assert.Equal(0, writer.LastRevision);
        Assert.Equal(1, writer.Heartbeat(_now).Revision);
    }

    [Fact]
    public void RevisionsRiseByExactlyOneAcrossEveryKind()
    {
        KodoStreamWriter writer = new();
        List<long> revisions =
        [
            writer.Heartbeat(_now).Revision,
            writer.StateChanged(KodoState.Healthy, KodoApiTestData.Evaluation(KodoState.Degraded), _now).Revision,
            writer.Heartbeat(_now.AddSeconds(10)).Revision,
            writer.StateChanged(KodoState.Degraded, KodoApiTestData.Evaluation(KodoState.Failing), _now.AddSeconds(20)).Revision,
        ];

        Assert.Equal([1L, 2L, 3L, 4L], revisions);
        Assert.Equal(4, writer.LastRevision);
    }

    [Fact]
    public void NewStreamStartsAfterTheRevisionItWasGiven()
    {
        KodoStreamWriter writer = new(lastRevision: 41);

        Assert.Equal(41, writer.LastRevision);
        Assert.Equal(42, writer.Heartbeat(_now).Revision);
        Assert.Equal(43, writer.Heartbeat(_now).Revision);
    }

    [Fact]
    public void RejectsANegativeStartingRevision()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new KodoStreamWriter(lastRevision: -1));
    }

    [Fact]
    public void ConcurrentPublishersGetUniqueGapFreeRevisions()
    {
        KodoStreamWriter writer = new();
        long[] revisions = new long[2000];

        Parallel.For(0, revisions.Length, index =>
        {
            revisions[index] = writer.Heartbeat(_now).Revision;
        });

        Assert.Equal(Enumerable.Range(1, revisions.Length).Select(value => (long)value), revisions.Order());
        Assert.Equal(revisions.Length, writer.LastRevision);
    }

    [Fact]
    public void StateChangedEventNamesThePreviousAndNewStates()
    {
        KodoStreamWriter writer = new();

        KodoStreamEventDto streamEvent = writer.StateChanged(
            KodoState.Degraded, KodoApiTestData.Evaluation(KodoState.Failing), _now);

        Assert.Equal(KodoStreamEventDto.StateChangedType, streamEvent.Type);
        Assert.Equal(_now, streamEvent.At);
        Assert.Null(streamEvent.Snapshot);
        Assert.Equal(new KodoStateChangeDto("degraded", "failing", null), streamEvent.StateChange);
    }

    [Fact]
    public void StateChangedWhileFrozenKeepsTheUnderlyingStateInThePayload()
    {
        KodoStreamWriter writer = new();

        KodoStreamEventDto streamEvent = writer.StateChanged(
            KodoState.Failing, KodoApiTestData.Evaluation(KodoState.Frozen, KodoState.Failing), _now);

        Assert.Equal(new KodoStateChangeDto("failing", "frozen", "failing"), streamEvent.StateChange);
    }

    [Fact]
    public void StateChangedRejectsAnUnchangedDisplayedState()
    {
        KodoStreamWriter writer = new();

        Assert.Throws<ArgumentException>(() =>
            writer.StateChanged(KodoState.Healthy, KodoApiTestData.Evaluation(KodoState.Healthy), _now));
    }

    [Fact]
    public void RejectedStateChangeDoesNotUseARevision()
    {
        KodoStreamWriter writer = new();

        Assert.Throws<ArgumentException>(() =>
            writer.StateChanged(KodoState.Healthy, KodoApiTestData.Evaluation(KodoState.Healthy), _now));

        Assert.Equal(0, writer.LastRevision);
    }

    [Fact]
    public void HeartbeatCarriesNoPayload()
    {
        KodoStreamEventDto streamEvent = new KodoStreamWriter().Heartbeat(_now);

        Assert.Equal(KodoStreamEventDto.HeartbeatType, streamEvent.Type);
        Assert.Null(streamEvent.StateChange);
        Assert.Null(streamEvent.Snapshot);
    }

    [Fact]
    public void SnapshotCarriesTheCurrentRevisionAndDoesNotRaiseIt()
    {
        KodoStreamWriter writer = new();
        writer.Heartbeat(_now);

        KodoStreamEventDto streamEvent = writer.Snapshot(revision => KodoApiTestData.Snapshot(revision), _now);

        Assert.Equal(KodoStreamEventDto.SnapshotType, streamEvent.Type);
        Assert.Equal(1, streamEvent.Revision);
        Assert.Equal(1, streamEvent.Snapshot!.Revision);
        Assert.Null(streamEvent.StateChange);
        Assert.Equal(1, writer.LastRevision);
    }

    [Fact]
    public void SnapshotOnANewStreamIsRevisionZero()
    {
        KodoStreamEventDto streamEvent = new KodoStreamWriter().Snapshot(revision => KodoApiTestData.Snapshot(revision), _now);

        Assert.Equal(0, streamEvent.Revision);
        Assert.Equal(0, streamEvent.Snapshot!.Revision);
    }

    [Fact]
    public void SubscribersConnectingAtDifferentTimesLeaveNoRevisionGaps()
    {
        KodoStreamWriter writer = new();
        List<long> deltas = [];
        List<long> snapshots = [];

        deltas.Add(writer.Heartbeat(_now).Revision);
        snapshots.Add(writer.Snapshot(revision => KodoApiTestData.Snapshot(revision), _now).Revision);
        snapshots.Add(writer.Snapshot(revision => KodoApiTestData.Snapshot(revision), _now).Revision);
        deltas.Add(writer.Heartbeat(_now).Revision);
        snapshots.Add(writer.Snapshot(revision => KodoApiTestData.Snapshot(revision), _now).Revision);
        deltas.Add(writer.StateChanged(KodoState.Healthy, KodoApiTestData.Evaluation(KodoState.Failing), _now).Revision);
        snapshots.Add(writer.Snapshot(revision => KodoApiTestData.Snapshot(revision), _now).Revision);

        // Deltas stay 1, 2, 3: a subscriber that took a snapshot never saw a number skipped.
        Assert.Equal([1L, 2L, 3L], deltas);
        Assert.Equal([1L, 1L, 2L, 3L], snapshots);
        Assert.Equal(3, writer.LastRevision);
    }

    [Fact]
    public void SnapshotBuilderGetsTheRevisionTheEventIsStampedWith()
    {
        KodoStreamWriter writer = new(lastRevision: 9);
        long seen = -1;

        KodoStreamEventDto streamEvent = writer.Snapshot(
            revision =>
            {
                seen = revision;
                return KodoApiTestData.Snapshot(revision);
            },
            _now);

        Assert.Equal(9, seen);
        Assert.Equal(9, streamEvent.Snapshot!.Revision);
    }

    [Fact]
    public void SnapshotThatIgnoresTheGivenRevisionIsAProgrammingError()
    {
        KodoStreamWriter writer = new();
        writer.Heartbeat(_now);

        Assert.Throws<InvalidOperationException>(() => writer.Snapshot(_ => KodoApiTestData.Snapshot(7), _now));
        Assert.Equal(1, writer.LastRevision);
    }

    [Fact]
    public async Task ConcurrentPublishersAndSnapshotTakersNeverThrowAndStayConsistent()
    {
        KodoStreamWriter writer = new();
        const int publishers = 8;
        const int perPublisher = 500;
        List<long>[] taken = [.. Enumerable.Range(0, 4).Select(_ => new List<long>())];
        long[] deltaRevisions = new long[publishers * perPublisher];
        bool done = false;

        Task[] takers = [.. taken.Select(seen => Task.Run(() =>
        {
            while (!Volatile.Read(ref done))
            {
                KodoStreamEventDto streamEvent = writer.Snapshot(revision => KodoApiTestData.Snapshot(revision), _now);
                Assert.Equal(streamEvent.Revision, streamEvent.Snapshot!.Revision);
                seen.Add(streamEvent.Revision);
            }
        }))];

        Parallel.For(0, deltaRevisions.Length, new ParallelOptions { MaxDegreeOfParallelism = publishers }, index =>
        {
            deltaRevisions[index] = writer.Heartbeat(_now).Revision;
        });

        Volatile.Write(ref done, true);
        await Task.WhenAll(takers);

        Assert.Equal(Enumerable.Range(1, deltaRevisions.Length).Select(value => (long)value), deltaRevisions.Order());
        Assert.Equal(deltaRevisions.Length, writer.LastRevision);
        foreach (List<long> seen in taken)
        {
            // Each taker sees a revision that never goes backwards and never passes the last delta.
            Assert.Equal(seen.Order(), seen);
            Assert.All(seen, revision => Assert.InRange(revision, 0, deltaRevisions.Length));
        }
    }

    [Fact]
    public void RejectsANullSnapshotBuilder()
    {
        Assert.Throws<ArgumentNullException>(() => new KodoStreamWriter().Snapshot(null!, _now));
    }

    [Fact]
    public void ExhaustedStreamRefusesToNumberMoreEvents()
    {
        KodoStreamWriter writer = new(lastRevision: long.MaxValue - 1);

        Assert.Equal(long.MaxValue, writer.Heartbeat(_now).Revision);
        Assert.Throws<InvalidOperationException>(() => writer.Heartbeat(_now));
    }
}
