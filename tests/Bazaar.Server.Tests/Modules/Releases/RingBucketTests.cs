using Bazaar.Server.Modules.Releases;

namespace Bazaar.Server.Tests.Modules.Releases;

/// <summary>Deterministic ring buckets (doc 49 section 6). Pure.</summary>
public sealed class RingBucketTests
{
    private static IEnumerable<string> SyntheticIds(int count) =>
        Enumerable.Range(1, count).Select(i => $"install-{i:D5}");

    [Fact]
    public void Bucket_is_a_fixed_value_across_runs()
    {
        // Golden value: SHA-256("install-0001") first 8 bytes, big-endian, mod 10000, computed independently.
        Assert.Equal(5737, RingBucket.BucketOf("install-0001"));
        Assert.Equal(RingBucket.BucketOf("install-0001"), RingBucket.BucketOf("install-0001"));
    }

    [Fact]
    public void Bucket_is_always_inside_the_bucket_range()
    {
        foreach (var id in SyntheticIds(10_000))
        {
            var bucket = RingBucket.BucketOf(id);
            Assert.InRange(bucket, 0, RingBucket.BucketCount - 1);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(5)]
    [InlineData(10)]
    [InlineData(50)]
    [InlineData(99)]
    public void Ring_share_is_close_to_its_percentage_over_10000_ids(int percent)
    {
        var ids = SyntheticIds(10_000).ToList();

        var inRing = ids.Count(id => RingBucket.IsInRing(id, percent));

        // Expected count is percent * 100. Allow 3 standard deviations for a binomial sample, with a floor of 20.
        var expected = percent * 100.0;
        var p = percent / 100.0;
        var tolerance = Math.Max(20, 3 * Math.Sqrt(10_000 * p * (1 - p)));
        Assert.InRange(inRing, expected - tolerance, expected + tolerance);
    }

    [Fact]
    public void Zero_percent_admits_nobody_and_hundred_percent_admits_everyone()
    {
        foreach (var id in SyntheticIds(1_000))
        {
            Assert.False(RingBucket.IsInRing(id, 0));
            Assert.True(RingBucket.IsInRing(id, 100));
        }
    }

    [Fact]
    public void Smaller_ring_is_nested_inside_larger_rings()
    {
        // Canary (1 percent) is inside Early (5 percent), which is inside Broad (50 percent). No install moves out as a ring widens.
        foreach (var id in SyntheticIds(10_000))
        {
            if (RingBucket.IsInRing(id, 1))
            {
                Assert.True(RingBucket.IsInRing(id, 5));
            }

            if (RingBucket.IsInRing(id, 5))
            {
                Assert.True(RingBucket.IsInRing(id, 50));
            }
        }
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(101)]
    public void Percent_outside_zero_to_hundred_is_rejected(int percent)
    {
        Assert.ThrowsAny<ArgumentOutOfRangeException>(() => RingBucket.IsInRing("install-0001", percent));
    }

    [Fact]
    public void Null_or_empty_install_id_is_rejected()
    {
        Assert.Throws<ArgumentNullException>(() => RingBucket.BucketOf(null!));
        Assert.Throws<ArgumentException>(() => RingBucket.BucketOf(string.Empty));
        Assert.Throws<ArgumentException>(() => RingBucket.IsInRing(string.Empty, 10));
    }
}
