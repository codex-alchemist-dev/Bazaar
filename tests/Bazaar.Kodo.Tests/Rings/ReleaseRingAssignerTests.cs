using Bazaar.Kodo.Rings;

namespace Bazaar.Kodo.Tests.Rings;

public sealed class ReleaseRingAssignerTests
{
    private static IEnumerable<string> Devices(int count) => Enumerable.Range(0, count).Select(i => $"device-{i}");

    [Fact]
    public void AssignmentIsDeterministicAndOneRingPerDevice()
    {
        RingOptions options = new();
        IReadOnlyDictionary<string, ReleaseRing> first = ReleaseRingAssigner.AssignFleet("v0.2.0", Devices(2000), options);
        IReadOnlyDictionary<string, ReleaseRing> second = ReleaseRingAssigner.AssignFleet("v0.2.0", Devices(2000).Reverse(), options);

        Assert.Equal(2000, first.Count);
        Assert.All(first, pair => Assert.Equal(pair.Value, second[pair.Key]));
        Assert.All(first, pair => Assert.Equal(pair.Value, ReleaseRingAssigner.RingFor("v0.2.0", pair.Key, options)));
    }

    [Fact]
    public void SharesFollowTheConfiguredPercentages()
    {
        IReadOnlyDictionary<string, ReleaseRing> rings = ReleaseRingAssigner.AssignFleet("v0.2.0", Devices(20_000), new RingOptions());
        double Share(ReleaseRing ring) => rings.Values.Count(value => value == ring) / 20_000.0;

        Assert.InRange(Share(ReleaseRing.Canary), 0.02, 0.04);
        Assert.InRange(Share(ReleaseRing.Early), 0.22, 0.28);
        Assert.InRange(Share(ReleaseRing.Broad), 0.68, 0.75);
    }

    [Fact]
    public void DifferentReleasesReshuffleTheFleet()
    {
        RingOptions options = new();
        IReadOnlyDictionary<string, ReleaseRing> a = ReleaseRingAssigner.AssignFleet("v0.2.0", Devices(2000), options);
        IReadOnlyDictionary<string, ReleaseRing> b = ReleaseRingAssigner.AssignFleet("v0.3.0", Devices(2000), options);

        Assert.Contains(a, pair => b[pair.Key] != pair.Value);
    }

    [Fact]
    public void ATinyFleetStillGetsACanary()
    {
        IReadOnlyDictionary<string, ReleaseRing> rings = ReleaseRingAssigner.AssignFleet("v0.2.0", ["only-device"], new RingOptions());

        Assert.Equal(ReleaseRing.Canary, rings["only-device"]);
        Assert.Empty(ReleaseRingAssigner.AssignFleet("v0.2.0", [], new RingOptions()));
    }

    [Theory]
    [InlineData(0, 2500)]
    [InlineData(1001, 2500)]
    [InlineData(300, 200)]
    [InlineData(1000, 5500)]
    public void OutOfRangeSizesAreRefused(int canary, int early)
    {
        RingOptions options = new() { CanaryBasisPoints = canary, EarlyBasisPoints = early };

        Assert.Throws<ArgumentOutOfRangeException>(options.Validate);
        Assert.Throws<ArgumentOutOfRangeException>(() => ReleaseRingAssigner.RingFor("v0.2.0", "d", options));
    }
}
