using Bazaar.Server.Host.Startup;

namespace Bazaar.Server.Host.Tests;

public sealed class HostStateTests
{
    /// <summary>A wall clock that can be stepped, and a monotonic clock that cannot.</summary>
    private sealed class SteppedClock : TimeProvider
    {
        public DateTimeOffset Wall { get; set; } = new(2026, 11, 19, 5, 0, 0, TimeSpan.Zero);

        public long Ticks { get; set; }

        public override DateTimeOffset GetUtcNow() => Wall;

        public override long GetTimestamp() => Ticks;

        public override long TimestampFrequency => 1000;
    }

    [Theory]
    [InlineData(-3650)]
    [InlineData(3650)]
    public void Uptime_ignores_wall_clock_steps(int stepDays)
    {
        // Chaos campaign disk-clock-resources: /status reported uptime_seconds -343457999 after the clock stepped back ten years.
        var clock = new SteppedClock();
        var state = new HostState(clock);

        clock.Ticks += 5_000; // five real seconds pass...
        clock.Wall = clock.Wall.AddDays(stepDays); // ...and the wall clock jumps

        Assert.Equal(TimeSpan.FromSeconds(5), state.Uptime);
    }
}
