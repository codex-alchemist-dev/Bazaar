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
        Assert.Equal(1, writer.NextRevision);
        Assert.Equal(1, writer.Heartbeat(_now).Revision);
    }

    [Fact]
    public void RevisionsRiseByExactlyOneAcrossEveryKind()
    {
        KodoStreamWriter writer = new();
        List<long> revisions =
        [
            writer.Snapshot(KodoApiTestData.Snapshot(writer.NextRevision)).Revision,
            writer.Heartbeat(_now).Revision,
            writer.StateChanged(KodoState.Healthy, KodoApiTestData.Evaluation(KodoState.Degraded), _now).Revision,
            writer.Heartbeat(_now.AddSeconds(10)).Revision,
            writer.StateChanged(KodoState.Degraded, KodoApiTestData.Evaluation(KodoState.Failing), _now.AddSeconds(20)).Revision,
        ];

        Assert.Equal([1L, 2L, 3L, 4L, 5L], revisions);
        Assert.Equal(5, writer.LastRevision);
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

        KodoStreamEvent streamEvent = writer.StateChanged(
            KodoState.Degraded, KodoApiTestData.Evaluation(KodoState.Failing), _now);

        Assert.Equal(KodoStreamEvent.StateChangedType, streamEvent.Type);
        Assert.Equal(_now, streamEvent.At);
        Assert.Null(streamEvent.Snapshot);
        Assert.Equal(new KodoStateChange("degraded", "failing", "failing"), streamEvent.StateChange);
    }

    [Fact]
    public void StateChangedWhileFrozenKeepsTheUnderlyingStateInThePayload()
    {
        KodoStreamWriter writer = new();

        KodoStreamEvent streamEvent = writer.StateChanged(
            KodoState.Failing, KodoApiTestData.Evaluation(KodoState.Frozen, KodoState.Failing), _now);

        Assert.Equal(new KodoStateChange("failing", "frozen", "failing"), streamEvent.StateChange);
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
        KodoStreamEvent streamEvent = new KodoStreamWriter().Heartbeat(_now);

        Assert.Equal(KodoStreamEvent.HeartbeatType, streamEvent.Type);
        Assert.Null(streamEvent.StateChange);
        Assert.Null(streamEvent.Snapshot);
    }

    [Fact]
    public void SnapshotMustReflectTheRevisionItIsSentWith()
    {
        KodoStreamWriter writer = new();
        writer.Heartbeat(_now);

        // Revision 1 is taken, so the next event is 2. A snapshot that says 1 would tell a client to apply deltas after 1 twice.
        Assert.Throws<ArgumentException>(() => writer.Snapshot(KodoApiTestData.Snapshot(1)));
        Assert.Equal(1, writer.LastRevision);
    }

    [Fact]
    public void SnapshotOnItsOwnRevisionIsAcceptedAndCounts()
    {
        KodoStreamWriter writer = new();
        KodoStreamEvent streamEvent = writer.Snapshot(KodoApiTestData.Snapshot(writer.NextRevision));

        Assert.Equal(KodoStreamEvent.SnapshotType, streamEvent.Type);
        Assert.Equal(1, streamEvent.Revision);
        Assert.Equal(1, streamEvent.Snapshot!.Revision);
        Assert.Null(streamEvent.StateChange);
        Assert.Equal(1, writer.LastRevision);
    }

    [Fact]
    public void RejectsANullSnapshot()
    {
        Assert.Throws<ArgumentNullException>(() => new KodoStreamWriter().Snapshot(null!));
    }

    [Fact]
    public void ExhaustedStreamRefusesToNumberMoreEvents()
    {
        KodoStreamWriter writer = new(lastRevision: long.MaxValue - 1);

        Assert.Equal(long.MaxValue, writer.Heartbeat(_now).Revision);
        Assert.Throws<InvalidOperationException>(() => writer.Heartbeat(_now));
    }
}
