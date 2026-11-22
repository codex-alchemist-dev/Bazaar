using Bazaar.Client.GitSolarSystem.Abstractions;
using Bazaar.Contracts.Graph;

namespace Bazaar.Client.GitSolarSystem.Tests;

public sealed class GitSolarStateTests
{
    private static GitSolarState StateWithMergeSnapshot(long version = 1)
    {
        var state = new GitSolarState();
        Assert.True(state.Apply(new GitSolarUpdate.Snapshot(GitSolarFixtures.MergeWithComet(version))));
        return state;
    }

    [Fact]
    public void AcceptedFrameIsPackedToTwoDimensions()
    {
        GitSolarState state = StateWithMergeSnapshot();
        float[] xyz = [1f, 2f, 9f, 3f, 4f, 9f, 5f, 6f, 9f];

        bool changed = state.Apply(new GitSolarUpdate.Frame(GitSolarFixtures.Frame(1, 1, xyz)));

        Assert.True(changed);
        Assert.NotNull(state.LatestFrame);
        Assert.Equal(2, state.LatestFrame.Dimensions);
        Assert.Equal(new float[] { 1, 2, 3, 4, 5, 6 }, state.LatestFrame.Positions.ToArray());
        Assert.Equal(1, state.LatestFrame.Epoch);
        Assert.Equal(1, state.LatestFrame.Sequence);
    }

    [Fact]
    public void StaleFrameIsDropped()
    {
        GitSolarState state = StateWithMergeSnapshot();
        float[] xyz = new float[9];
        Assert.True(state.Apply(new GitSolarUpdate.Frame(GitSolarFixtures.Frame(1, 5, xyz))));

        bool changed = state.Apply(new GitSolarUpdate.Frame(GitSolarFixtures.Frame(1, 3, xyz)));

        Assert.False(changed);
        Assert.Equal(5, state.LatestFrame!.Sequence);
    }

    [Fact]
    public void FrameWithWrongVersionIsRefused()
    {
        GitSolarState state = StateWithMergeSnapshot();

        bool changed = state.Apply(new GitSolarUpdate.Frame(GitSolarFixtures.Frame(2, 1, new float[9])));

        Assert.False(changed);
        Assert.Null(state.LatestFrame);
    }

    [Fact]
    public void FrameWithWrongLengthIsRefused()
    {
        GitSolarState state = StateWithMergeSnapshot();

        bool changed = state.Apply(new GitSolarUpdate.Frame(GitSolarFixtures.Frame(1, 1, new float[6])));

        Assert.False(changed);
        Assert.Null(state.LatestFrame);
    }

    [Fact]
    public void FrameWithNonFinitePositionIsRefusedWithoutThrowing()
    {
        GitSolarState state = StateWithMergeSnapshot();
        float[] xyz = [0f, 0f, 0f, float.NaN, 0f, 0f, 0f, 0f, 0f];

        bool changed = state.Apply(new GitSolarUpdate.Frame(GitSolarFixtures.Frame(1, 1, xyz)));

        Assert.False(changed);
        Assert.Null(state.LatestFrame);
    }

    [Fact]
    public void FrameBeforeAnySnapshotIsRefused()
    {
        var state = new GitSolarState();

        bool changed = state.Apply(new GitSolarUpdate.Frame(GitSolarFixtures.Frame(1, 1, new float[9])));

        Assert.False(changed);
        Assert.Null(state.Scene);
    }

    [Fact]
    public void DisconnectMarksStaleAndSnapshotClearsIt()
    {
        GitSolarState state = StateWithMergeSnapshot();

        Assert.True(state.Apply(new GitSolarUpdate.Disconnected()));
        Assert.True(state.IsStale);
        Assert.NotNull(state.Scene);

        Assert.True(state.Apply(new GitSolarUpdate.Snapshot(GitSolarFixtures.MergeWithComet(1))));
        Assert.False(state.IsStale);
    }

    [Fact]
    public void FrameAlsoClearsStale()
    {
        GitSolarState state = StateWithMergeSnapshot();
        state.Apply(new GitSolarUpdate.Disconnected());

        Assert.True(state.Apply(new GitSolarUpdate.Frame(GitSolarFixtures.Frame(1, 1, new float[9]))));
        Assert.False(state.IsStale);
    }

    [Fact]
    public void OlderSnapshotIsIgnored()
    {
        GitSolarState state = StateWithMergeSnapshot(version: 4);

        bool changed = state.Apply(new GitSolarUpdate.Snapshot(GitSolarFixtures.MergeWithComet(version: 3)));

        Assert.False(changed);
        Assert.Equal(4, state.Scene!.GraphVersion);
    }

    [Fact]
    public void NewVersionResetsSequencerAndDropsOldFrame()
    {
        GitSolarState state = StateWithMergeSnapshot(version: 1);
        Assert.True(state.Apply(new GitSolarUpdate.Frame(GitSolarFixtures.Frame(1, 9, new float[9]))));

        Assert.True(state.Apply(new GitSolarUpdate.Snapshot(GitSolarFixtures.MergeWithComet(version: 2))));
        Assert.Null(state.LatestFrame);
        Assert.True(state.Apply(new GitSolarUpdate.Frame(GitSolarFixtures.Frame(2, 1, new float[9]))));
        Assert.Equal(2, state.LatestFrame!.Epoch);
    }

    [Fact]
    public void SnapshotWithDuplicateShaIsRefused()
    {
        var state = new GitSolarState();

        bool changed = state.Apply(new GitSolarUpdate.Snapshot(GitSolarFixtures.Linear(("A", 1), ("A", 2))));

        Assert.False(changed);
        Assert.Null(state.Scene);
    }
}
