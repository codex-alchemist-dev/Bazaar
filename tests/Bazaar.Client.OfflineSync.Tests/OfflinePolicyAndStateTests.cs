using Bazaar.Client.OfflineSync;
using Bazaar.OfflineSync;

namespace Bazaar.Client.OfflineSync.Tests;

public sealed class OfflinePolicyTests
{
    private static readonly string[] _expectedIds = ["settings", "company-locks", "agent-memory", "passport", "secrets"];

    [Fact]
    public void IsWritableOffline_IsTrueForEveryDataClass()
    {
        foreach (var dataClass in Enum.GetValues<OfflineDataClass>())
        {
            Assert.True(OfflinePolicy.IsWritableOffline(dataClass), dataClass.ToString());
        }
    }

    [Fact]
    public void DisabledControls_CoverTheServerHeldAreas_WithReasons()
    {
        var ids = OfflinePolicy.DisabledControls.Select(c => c.ControlId).ToArray();

        Assert.Equal(_expectedIds, ids);
        Assert.All(OfflinePolicy.DisabledControls, c => Assert.False(string.IsNullOrWhiteSpace(c.Reason)));
    }

    [Fact]
    public void DisabledReason_ReturnsTheReasonForListedControls_AndNullForOthers()
    {
        Assert.Equal(OfflinePolicy.DisabledControls[0].Reason, OfflinePolicy.DisabledReason("settings"));
        Assert.Null(OfflinePolicy.DisabledReason("gc-message"));
    }
}

public sealed class ConnectionStateTrackerTests
{
    private static readonly DateTimeOffset _asOf = new(2026, 11, 18, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void StartsLive_AndRaisesOnlyOnRealChanges()
    {
        var tracker = new ConnectionStateTracker();
        var raised = new List<ConnectionStateChangedEventArgs>();
        tracker.StateChanged += (_, e) => raised.Add(e);

        Assert.IsType<ConnectionState.Live>(tracker.Current);
        tracker.Set(new ConnectionState.Live());
        tracker.Set(new ConnectionState.Offline(_asOf));
        tracker.Set(new ConnectionState.Offline(_asOf));
        tracker.Set(new ConnectionState.Syncing(0, 2));

        Assert.Equal(2, raised.Count);
        Assert.Equal(new ConnectionState.Offline(_asOf), raised[0].Current);
        Assert.Equal(new ConnectionState.Syncing(0, 2), tracker.Current);
    }

    [Fact]
    public void ConcurrentSets_DoNotFail()
    {
        var tracker = new ConnectionStateTracker();
        Parallel.For(0, 8, worker =>
        {
            for (var i = 0; i < 1_000; i++)
            {
                tracker.Set(new ConnectionState.Syncing(i % 5, 5));
                tracker.Set(new ConnectionState.Attention(worker));
            }
        });

        Assert.NotNull(tracker.Current);
    }

    [Fact]
    public void SyncingProgress_IsRecordedAsGiven()
    {
        var state = new ConnectionState.Syncing(1, 3);
        Assert.Equal(1, state.Done);
        Assert.Equal(3, state.Total);
    }
}
