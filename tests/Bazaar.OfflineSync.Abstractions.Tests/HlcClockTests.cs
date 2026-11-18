using System.Collections.Concurrent;

namespace Bazaar.OfflineSync.Tests;

/// <summary>Behaviour tests for <see cref="HlcClock"/> (doc 56 section 3, standard HLC algorithm).</summary>
public sealed class HlcClockTests
{
    private static readonly Guid _device = Guid.Parse("0000000a-0000-0000-0000-000000000000");
    private static readonly Guid _remote = Guid.Parse("0000000b-0000-0000-0000-000000000000");

    [Fact]
    public void TickStampsCarryTheClockDevice()
    {
        var clock = new HlcClock(_device);

        var stamp = clock.Tick(1_000);

        Assert.Equal(_device, clock.DeviceId);
        Assert.Equal(_device, stamp.DeviceId);
        Assert.Equal(1_000, stamp.WallMillis);
        Assert.Equal(0, stamp.Counter);
    }

    [Fact]
    public void TickWithSameWallClockIncrementsCounter()
    {
        var clock = new HlcClock(_device);

        var first = clock.Tick(1_000);
        var second = clock.Tick(1_000);
        var third = clock.Tick(1_000);

        Assert.Equal(1_000, second.WallMillis);
        Assert.Equal(1, second.Counter);
        Assert.Equal(2, third.Counter);
        Assert.True(first < second);
        Assert.True(second < third);
    }

    [Fact]
    public void TickResetsCounterWhenWallClockAdvances()
    {
        var clock = new HlcClock(_device);
        clock.Tick(1_000);
        clock.Tick(1_000);

        var advanced = clock.Tick(1_001);

        Assert.Equal(1_001, advanced.WallMillis);
        Assert.Equal(0, advanced.Counter);
    }

    [Fact]
    public void TickStaysMonotonicWhenWallClockGoesBackwards()
    {
        var clock = new HlcClock(_device);
        var before = clock.Tick(1_000);

        var backwards = clock.Tick(500);
        var stillBackwards = clock.Tick(400);
        var forwardAgain = clock.Tick(1_000);

        Assert.True(backwards > before);
        Assert.True(stillBackwards > backwards);
        Assert.True(forwardAgain > stillBackwards);
        Assert.Equal(1_000, backwards.WallMillis);
        Assert.Equal(1_000, stillBackwards.WallMillis);
    }

    [Fact]
    public void TickSeededFromInitialStateNeverGoesBehindIt()
    {
        var initial = new Hlc(5_000, 3, _remote);
        var clock = new HlcClock(_device, initial);

        var stamp = clock.Tick(4_000);

        Assert.Equal(5_000, stamp.WallMillis);
        Assert.Equal(4, stamp.Counter);
        Assert.True(stamp > initial);
    }

    [Fact]
    public void ReceiveFromFutureRemoteAdvancesPastIt()
    {
        var clock = new HlcClock(_device);
        clock.Tick(1_000);
        var remote = new Hlc(9_000, 7, _remote);

        var received = clock.Receive(remote, 1_000);

        Assert.True(received > remote);
        Assert.Equal(9_000, received.WallMillis);
        Assert.Equal(8, received.Counter);
        Assert.Equal(_device, received.DeviceId);

        var next = clock.Tick(1_000);
        Assert.True(next > received);
    }

    [Fact]
    public void ReceiveFromPastRemoteDoesNotRewindTheClock()
    {
        var clock = new HlcClock(_device);
        var local = clock.Tick(5_000);
        var remote = new Hlc(1_000, 50, _remote);

        var received = clock.Receive(remote, 2_000);

        Assert.True(received > local);
        Assert.True(received > remote);
        Assert.Equal(5_000, received.WallMillis);
        Assert.Equal(local.Counter + 1, received.Counter);
    }

    [Fact]
    public void ReceiveWithAllWallsEqualTakesMaxCounterPlusOne()
    {
        var clock = new HlcClock(_device, new Hlc(5_000, 2, _device));
        var remote = new Hlc(5_000, 4, _remote);

        var received = clock.Receive(remote, 5_000);

        Assert.Equal(5_000, received.WallMillis);
        Assert.Equal(5, received.Counter);
        Assert.True(received > remote);
    }

    [Fact]
    public void ReceiveWithLocalWallWinningTiesUsesLocalCounterPlusOne()
    {
        // Local wall == now > remote wall: the local counter is bumped, the remote counter is ignored.
        var clock = new HlcClock(_device, new Hlc(5_000, 2, _device));
        var remote = new Hlc(4_000, 9, _remote);

        var received = clock.Receive(remote, 5_000);

        Assert.Equal(5_000, received.WallMillis);
        Assert.Equal(3, received.Counter);
        Assert.True(received > remote);
    }

    [Fact]
    public void ReceiveWithRemoteWallWinningTiesUsesRemoteCounterPlusOne()
    {
        // _remote wall == now > local wall: the remote counter is used, the local counter is ignored.
        var clock = new HlcClock(_device, new Hlc(4_000, 2, _device));
        var remote = new Hlc(5_000, 6, _remote);

        var received = clock.Receive(remote, 5_000);

        Assert.Equal(5_000, received.WallMillis);
        Assert.Equal(7, received.Counter);
        Assert.True(received > remote);
    }

    [Fact]
    public void ReceiveWithNowAheadOfBothResetsCounter()
    {
        var clock = new HlcClock(_device, new Hlc(4_000, 2, _device));
        var remote = new Hlc(3_000, 9, _remote);

        var received = clock.Receive(remote, 5_000);

        Assert.Equal(5_000, received.WallMillis);
        Assert.Equal(0, received.Counter);
        Assert.True(received > remote);
    }

    [Fact]
    public void ReceiveWithLocalAndRemoteTiedBelowNowResetsCounter()
    {
        var clock = new HlcClock(_device, new Hlc(4_000, 2, _device));
        var remote = new Hlc(4_000, 9, _remote);

        var received = clock.Receive(remote, 5_000);

        Assert.Equal(5_000, received.WallMillis);
        Assert.Equal(0, received.Counter);
    }

    [Fact]
    public void ReceiveWithAllWallsEqualAndSmallerRemoteCounterUsesLocalCounter()
    {
        var clock = new HlcClock(_device, new Hlc(5_000, 2, _device));
        var remote = new Hlc(5_000, 1, _remote);

        // All three walls equal: max(2, 1) + 1 = 3.
        var received = clock.Receive(remote, 5_000);

        Assert.Equal(3, received.Counter);
    }

    [Fact]
    public void ConcurrentTicksProduceUniqueStrictlyIncreasingStamps()
    {
        const int threads = 8;
        const int perThread = 2_000;
        var clock = new HlcClock(_device);
        var all = new ConcurrentBag<Hlc>();
        var perThreadLists = new ConcurrentBag<List<Hlc>>();

        Parallel.For(0, threads, t =>
        {
            var mine = new List<Hlc>(perThread);
            for (var i = 0; i < perThread; i++)
            {
                // Mix a slowly advancing wall clock with bursts at the same millisecond.
                var stamp = clock.Tick(1_000 + (i / 100) + t % 2);
                mine.Add(stamp);
                all.Add(stamp);
            }

            perThreadLists.Add(mine);
        });

        var distinct = all.Distinct().Count();
        Assert.Equal(threads * perThread, distinct);

        foreach (var list in perThreadLists)
        {
            for (var i = 1; i < list.Count; i++)
            {
                Assert.True(list[i] > list[i - 1]);
            }
        }
    }

    [Fact]
    public void ConcurrentMixedTicksAndReceivesStayUniqueAndIncreasing()
    {
        var clock = new HlcClock(_device);
        var stamps = new ConcurrentBag<Hlc>();

        Parallel.For(0, 8, t =>
        {
            var rng = new Random(52_100 + t);
            for (var i = 0; i < 1_000; i++)
            {
                if (i % 3 == 0)
                {
                    var remote = new Hlc(rng.Next(0, 3_000), rng.Next(0, 5), _remote);
                    stamps.Add(clock.Receive(remote, rng.Next(0, 3_000)));
                }
                else
                {
                    stamps.Add(clock.Tick(rng.Next(0, 3_000)));
                }
            }
        });

        Assert.Equal(8 * 1_000, stamps.Distinct().Count());
    }

    [Fact]
    public void MergingStampsInAnyOrderNeverProducesAStampBelowAnyInput()
    {
        var rng = new Random(20_261_118);

        for (var iteration = 0; iteration < 1_000; iteration++)
        {
            var initial = rng.Next(0, 2) == 0 ? (Hlc?)null : RandomStamp(rng, _device);
            var clockDevice = _device;
            var inputs = Enumerable.Range(0, rng.Next(1, 11)).Select(_ => RandomStamp(rng, _remote)).ToList();
            var now = rng.Next(0, 60);
            var order = inputs.OrderBy(_ => rng.Next()).ToList();

            var clock = new HlcClock(clockDevice, initial);
            var previous = initial ?? default;
            var maxInput = inputs.Max();
            var hasPrevious = initial.HasValue;

            foreach (var input in order)
            {
                var received = clock.Receive(input, now);

                Assert.True(received > input, $"iteration {iteration}: receive {received} not above input {input}");
                if (hasPrevious)
                {
                    Assert.True(received > previous, $"iteration {iteration}: clock went backwards");
                }

                previous = received;
                hasPrevious = true;
            }

            // The merged stamp must be at least every input, whatever order they arrived in.
            Assert.True(previous >= maxInput, $"iteration {iteration}: merged {previous} below max input {maxInput}");

            // Ticking after the merge stays above everything merged.
            var after = clock.Tick(rng.Next(0, 60));
            Assert.True(after > maxInput, $"iteration {iteration}: tick after merge {after} not above max input {maxInput}");
        }
    }

    private static Hlc RandomStamp(Random rng, Guid device)
    {
        // Small ranges make walls, counters and devices collide often.
        return new Hlc(rng.Next(0, 60), rng.Next(0, 5), rng.Next(0, 2) == 0 ? device : Guid.Parse("0000000c-0000-0000-0000-000000000000"));
    }
}
