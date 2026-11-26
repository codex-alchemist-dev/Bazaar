using Bazaar.Kodo.Slo;

namespace Bazaar.Kodo.Tests.Slo;

public sealed class SloBurnPolicyTests
{
    private static SloBurnRule Fast(int threshold = 6_000, int shortMin = 60, int longMin = 360) =>
        new("fast_burn", SloBurnSeverity.Critical, TimeSpan.FromMinutes(shortMin), TimeSpan.FromMinutes(longMin), threshold);

    private static SloBurnRule Slow(int threshold = 2_000, int shortMin = 60, int longMin = 360) =>
        new("slow_burn", SloBurnSeverity.Warning, TimeSpan.FromMinutes(shortMin), TimeSpan.FromMinutes(longMin), threshold);

    [Fact]
    public void The_defaults_use_the_one_hour_and_six_hour_windows()
    {
        Assert.Equal(2, SloBurnPolicy.Default.Rules.Count);
        Assert.All(SloBurnPolicy.Default.Rules, r =>
        {
            Assert.Equal(TimeSpan.FromHours(1), r.ShortWindow);
            Assert.Equal(TimeSpan.FromHours(6), r.LongWindow);
        });
        Assert.Equal(TimeSpan.FromHours(6), SloBurnPolicy.Default.LongestWindow);
    }

    [Fact]
    public void The_defaults_themselves_pass_the_tighten_check()
    {
        Assert.True(SloBurnPolicy.TryTighten([.. SloBurnPolicy.Default.Rules], out SloBurnPolicy? policy));
        Assert.Equal(2, policy!.Rules.Count);
    }

    [Fact]
    public void Lower_thresholds_and_shorter_windows_are_accepted()
    {
        Assert.True(SloBurnPolicy.TryTighten([Slow(1_500, 30, 180), Fast(3_000, 10, 120)], out SloBurnPolicy? policy));
        Assert.Equal(TimeSpan.FromHours(3), policy!.LongestWindow);
    }

    [Fact]
    public void Anything_looser_is_refused()
    {
        Assert.False(SloBurnPolicy.TryTighten([Fast(6_001), Slow()], out _));
        Assert.False(SloBurnPolicy.TryTighten([Fast(), Slow(2_001)], out _));
        Assert.False(SloBurnPolicy.TryTighten([Fast(shortMin: 61), Slow()], out _));
        Assert.False(SloBurnPolicy.TryTighten([Fast(longMin: 361), Slow()], out _));
    }

    [Fact]
    public void The_floors_and_shape_are_enforced()
    {
        Assert.False(SloBurnPolicy.TryTighten([Fast(999), Slow()], out _));
        Assert.False(SloBurnPolicy.TryTighten([Fast(shortMin: 0), Slow()], out _));
        Assert.False(SloBurnPolicy.TryTighten([Fast(shortMin: 30, longMin: 30), Slow()], out _));
        Assert.False(SloBurnPolicy.TryTighten([Fast(), Slow() with { Severity = SloBurnSeverity.Critical }], out _));
    }

    [Fact]
    public void The_rule_set_must_be_exactly_the_default_names()
    {
        Assert.False(SloBurnPolicy.TryTighten([Fast()], out _));
        Assert.False(SloBurnPolicy.TryTighten([Fast(), Fast()], out _));
        Assert.False(SloBurnPolicy.TryTighten([Fast(), Slow() with { Name = "other" }], out _));
        Assert.False(SloBurnPolicy.TryTighten([Fast(), Slow(), Slow() with { Name = "third" }], out _));
    }
}
