using Bazaar.Client.Core.Mobile;

namespace Bazaar.Client.Core.Tests.Mobile;

public sealed class PostureSourceTests
{
    private sealed class Recorder : IObserver<PostureKind>
    {
        public List<PostureKind> Seen { get; } = [];

        public void OnNext(PostureKind value) => Seen.Add(value);

        public void OnError(Exception error) => throw error;

        public void OnCompleted() => Completed = true;

        public bool Completed { get; private set; }
    }

    [Fact]
    public void Snapshot_mapping_matches_the_four_argument_rules()
    {
        Assert.Equal(PostureKind.Folded, FoldPosture.Map(FoldSnapshot.NoFold));
        Assert.Equal(PostureKind.Unfolded, FoldPosture.Map(new FoldSnapshot(true, FoldState.Flat, false, false)));
        Assert.Equal(PostureKind.HalfOpen, FoldPosture.Map(new FoldSnapshot(true, FoldState.HalfOpened, true, false)));
        Assert.Equal(PostureKind.Folded, FoldPosture.Map(new FoldSnapshot(true, FoldState.Flat, false, true)));
    }

    [Fact]
    public void Fake_starts_at_the_mapped_initial_posture()
    {
        using var source = new FakePostureSource(new FoldSnapshot(true, FoldState.Flat, false, false));

        Assert.Equal(PostureKind.Unfolded, source.Current);
    }

    [Fact]
    public void Fake_defaults_to_folded_without_a_snapshot()
    {
        using var source = new FakePostureSource();

        Assert.Equal(PostureKind.Folded, source.Current);
    }

    [Fact]
    public void Changes_emit_only_when_the_posture_really_changes()
    {
        using var source = new FakePostureSource(new FoldSnapshot(true, FoldState.Flat, false, false));
        var recorder = new Recorder();
        using var subscription = source.Changes.Subscribe(recorder);

        source.Push(new FoldSnapshot(true, FoldState.Flat, false, false));
        source.Push(new FoldSnapshot(true, FoldState.HalfOpened, true, false));
        source.Push(new FoldSnapshot(true, FoldState.HalfOpened, true, false));
        source.Push(new FoldSnapshot(true, FoldState.Flat, false, false));

        Assert.Equal([PostureKind.HalfOpen, PostureKind.Unfolded], recorder.Seen);
        Assert.Equal(PostureKind.Unfolded, source.Current);
    }

    [Fact]
    public void Disposing_the_subscription_stops_notifications()
    {
        using var source = new FakePostureSource();
        var recorder = new Recorder();
        var subscription = source.Changes.Subscribe(recorder);
        subscription.Dispose();

        source.Push(new FoldSnapshot(true, FoldState.Flat, false, false));

        Assert.Empty(recorder.Seen);
    }

    [Fact]
    public void Disposing_the_source_completes_subscribers_and_rejects_pushes()
    {
        var source = new FakePostureSource();
        var recorder = new Recorder();
        using var subscription = source.Changes.Subscribe(recorder);

        source.Dispose();

        Assert.True(recorder.Completed);
        Assert.Throws<ObjectDisposedException>(() => source.Push(FoldSnapshot.NoFold));
    }
}
