namespace Bazaar.OfflineSync.Tests;

/// <summary>Ordering tests for <see cref="Hlc"/> (doc 56 section 3): wall time, then counter, then device id.</summary>
public sealed class HlcTests
{
    private static readonly Guid _deviceLow = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private static readonly Guid _deviceHigh = Guid.Parse("00000000-0000-0000-0000-000000000002");

    [Fact]
    public void WallTimeDominatesCounterAndDevice()
    {
        var earlierWithBigCounter = new Hlc(1_000, 99, _deviceHigh);
        var laterWithZeroCounter = new Hlc(1_001, 0, _deviceLow);

        Assert.True(earlierWithBigCounter < laterWithZeroCounter);
        Assert.True(laterWithZeroCounter > earlierWithBigCounter);
        Assert.True(earlierWithBigCounter.CompareTo(laterWithZeroCounter) < 0);
    }

    [Fact]
    public void CounterBreaksWallTies()
    {
        var lowCounter = new Hlc(5_000, 1, _deviceHigh);
        var highCounter = new Hlc(5_000, 2, _deviceLow);

        Assert.True(lowCounter < highCounter);
        Assert.True(highCounter > lowCounter);
    }

    [Fact]
    public void DeviceIdBreaksWallAndCounterTies()
    {
        var a = new Hlc(5_000, 3, _deviceLow);
        var b = new Hlc(5_000, 3, _deviceHigh);

        Assert.True(a < b);
        Assert.True(b > a);
        Assert.NotEqual(0, a.CompareTo(b));
        Assert.Equal(Math.Sign(a.CompareTo(b)), -Math.Sign(b.CompareTo(a)));
    }

    [Fact]
    public void IdenticalStampsCompareEqual()
    {
        var a = new Hlc(5_000, 3, _deviceLow);
        var b = new Hlc(5_000, 3, _deviceLow);

        Assert.Equal(0, a.CompareTo(b));
        Assert.Equal(a, b);
        Assert.False(a < b);
        Assert.False(a > b);
        Assert.True(a <= b);
        Assert.True(a >= b);
    }

    [Fact]
    public void ComparisonOperatorsAgreeWithCompareTo()
    {
        var low = new Hlc(1, 0, _deviceLow);
        var high = new Hlc(2, 0, _deviceLow);

        Assert.True(low < high);
        Assert.True(low <= high);
        Assert.False(low > high);
        Assert.False(low >= high);

        Assert.True(high > low);
        Assert.True(high >= low);
        Assert.False(high < low);
        Assert.False(high <= low);

        var lowCopy = new Hlc(1, 0, _deviceLow);
        Assert.True(low <= lowCopy);
        Assert.True(low >= lowCopy);
    }

    [Fact]
    public void RandomStampsFormATotalOrder()
    {
        var rng = new Random(52_001);
        var stamps = Enumerable.Range(0, 500).Select(_ => RandomStamp(rng)).ToList();

        // Antisymmetry and totality: every pair is comparable in exactly one direction, and the operators agree.
        for (var i = 0; i < stamps.Count; i++)
        {
            for (var j = 0; j < stamps.Count; j++)
            {
                var a = stamps[i];
                var b = stamps[j];
                var c = a.CompareTo(b);

                Assert.Equal(Math.Sign(c), -Math.Sign(b.CompareTo(a)));
                Assert.Equal(c > 0, a > b);
                Assert.Equal(c < 0, a < b);
                Assert.Equal(c >= 0, a >= b);
                Assert.Equal(c <= 0, a <= b);
                Assert.Equal(c == 0, a.Equals(b));
            }
        }

        // Transitivity: a sorted list is consistent with every pair, whatever order it started in.
        var sorted = stamps.OrderBy(s => s).ToList();
        for (var i = 1; i < sorted.Count; i++)
        {
            Assert.True(sorted[i - 1] <= sorted[i]);
        }
    }

    [Fact]
    public void SortingAnyPermutationYieldsTheSameOrder()
    {
        var rng = new Random(52_002);
        var stamps = Enumerable.Range(0, 200).Select(_ => RandomStamp(rng)).ToList();
        var reference = stamps.OrderBy(s => s).ToList();

        for (var round = 0; round < 20; round++)
        {
            var shuffled = stamps.OrderBy(_ => rng.Next()).ToList();
            var sorted = shuffled.OrderBy(s => s).ToList();
            Assert.Equal(reference, sorted);
        }
    }

    private static Hlc RandomStamp(Random rng)
    {
        // Small ranges force wall, counter and device ties so every tiebreak is exercised.
        var wall = rng.Next(0, 6);
        var counter = rng.Next(0, 4);
        var device = rng.Next(0, 3) switch
        {
            0 => _deviceLow,
            1 => _deviceHigh,
            _ => Guid.Parse("00000000-0000-0000-0000-000000000003"),
        };
        return new Hlc(wall, counter, device);
    }
}
