using Bazaar.Wire.Abstractions;

namespace Bazaar.Client.Transport.AndroidHttp3.Android.Head.Tests;

public class CronetNetworkTrackerTests
{
    [Fact]
    public void First_network_is_not_an_event()
    {
        var tracker = new CronetNetworkTracker();
        Assert.Null(tracker.OnAvailable(1));
    }

    [Fact]
    public void Second_network_becoming_active_is_a_handover()
    {
        var tracker = new CronetNetworkTracker();
        tracker.OnAvailable(1);
        var e = tracker.OnAvailable(2);
        Assert.Equal(WireNetworkEventKind.Handover, e?.Kind);
    }

    [Fact]
    public void Losing_the_active_network_with_another_left_is_a_handover()
    {
        var tracker = new CronetNetworkTracker();
        tracker.OnAvailable(1);
        tracker.OnAvailable(2);
        Assert.Equal(WireNetworkEventKind.Handover, tracker.OnLost(2)?.Kind);
    }

    [Fact]
    public void Losing_a_background_network_is_silent()
    {
        var tracker = new CronetNetworkTracker();
        tracker.OnAvailable(1);
        tracker.OnAvailable(2);
        Assert.Null(tracker.OnLost(1));
    }

    [Fact]
    public void Losing_the_last_network_suspends_and_the_next_resumes()
    {
        var tracker = new CronetNetworkTracker();
        tracker.OnAvailable(1);
        Assert.Equal(WireNetworkEventKind.Suspended, tracker.OnLost(1)?.Kind);
        Assert.Equal(WireNetworkEventKind.Resumed, tracker.OnAvailable(7)?.Kind);
        Assert.Null(tracker.OnAvailable(7));
    }

    [Fact]
    public void Unknown_loss_is_ignored()
    {
        var tracker = new CronetNetworkTracker();
        Assert.Null(tracker.OnLost(99));
    }

    [Fact]
    public void Events_carry_the_clock_time_and_never_a_secret()
    {
        var at = new DateTimeOffset(2026, 11, 20, 1, 2, 3, TimeSpan.Zero);
        var tracker = new CronetNetworkTracker(new FixedTime(at));
        tracker.OnAvailable(1);
        var e = tracker.OnLost(1);
        Assert.Equal(at, e?.At);
        Assert.Equal("network 1 lost", e?.Detail);
    }

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
