using Bazaar.Server.Council.Pilot;

namespace Bazaar.Server.Tests.Council.Pilot;

public class HoldoutSelectorTests
{
    [Fact]
    public void Same_vote_and_seed_always_land_in_the_same_arm()
    {
        var first = HoldoutSelector.IsHoldout("vote-42", 0.15, "pilot-1");

        for (var i = 0; i < 100; i++)
        {
            Assert.Equal(first, HoldoutSelector.IsHoldout("vote-42", 0.15, "pilot-1"));
        }
    }

    [Fact]
    public void Holdout_share_is_about_fifteen_percent_over_ten_thousand_ids()
    {
        const int count = 10000;
        var held = 0;
        for (var i = 0; i < count; i++)
        {
            if (HoldoutSelector.IsHoldout($"vote-{i:D5}", 0.15, "pilot-1"))
            {
                held++;
            }
        }

        var share = (double)held / count;
        Assert.InRange(share, 0.15 - 0.02, 0.15 + 0.02);
    }

    [Fact]
    public void Changing_the_seed_changes_the_split()
    {
        var differing = Enumerable.Range(0, 1000).Count(i =>
            HoldoutSelector.IsHoldout($"vote-{i}", 0.5, "seed-a") != HoldoutSelector.IsHoldout($"vote-{i}", 0.5, "seed-b"));

        Assert.True(differing > 0);
    }

    [Fact]
    public void Share_zero_holds_nothing_and_share_one_holds_everything()
    {
        Assert.All(Enumerable.Range(0, 200), i => Assert.False(HoldoutSelector.IsHoldout($"vote-{i}", 0, "s")));
        Assert.All(Enumerable.Range(0, 200), i => Assert.True(HoldoutSelector.IsHoldout($"vote-{i}", 1, "s")));
    }

    [Theory]
    [InlineData(-0.01)]
    [InlineData(1.01)]
    [InlineData(double.NaN)]
    public void Share_outside_zero_to_one_is_rejected(double share)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => HoldoutSelector.IsHoldout("vote-1", share, "s"));
    }
}
