using Bazaar.Client.Athanor.Bridge;

namespace Bazaar.Client.Athanor.Tests.Bridge;

public sealed class BridgeSequenceGuardTests
{
    [Fact]
    public void A_new_guard_has_seen_nothing()
    {
        Assert.Equal(0, new BridgeSequenceGuard().LastSeq);
    }

    [Fact]
    public void Accepts_each_value_above_the_last()
    {
        var guard = new BridgeSequenceGuard();

        Assert.True(guard.TryAccept(1));
        Assert.True(guard.TryAccept(2));
        Assert.True(guard.TryAccept(50));
        Assert.Equal(50, guard.LastSeq);
    }

    [Fact]
    public void Accepts_a_gap_in_the_sequence()
    {
        var guard = new BridgeSequenceGuard();

        Assert.True(guard.TryAccept(1));
        Assert.True(guard.TryAccept(4));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Refuses_zero_and_negative_values_from_a_fresh_guard(long seq)
    {
        var guard = new BridgeSequenceGuard();

        Assert.False(guard.TryAccept(seq));
        Assert.Equal(0, guard.LastSeq);
    }

    [Fact]
    public void Refuses_a_repeat_and_leaves_the_last_value_alone()
    {
        var guard = new BridgeSequenceGuard();
        Assert.True(guard.TryAccept(3));

        Assert.False(guard.TryAccept(3));
        Assert.Equal(3, guard.LastSeq);
    }

    [Fact]
    public void Refuses_an_older_value_after_a_newer_one()
    {
        var guard = new BridgeSequenceGuard();
        Assert.True(guard.TryAccept(10));

        Assert.False(guard.TryAccept(9));
        Assert.Equal(10, guard.LastSeq);
        Assert.True(guard.TryAccept(11));
    }

    [Fact]
    public void Accepts_the_largest_long_value_once()
    {
        var guard = new BridgeSequenceGuard();

        Assert.True(guard.TryAccept(long.MaxValue));
        Assert.False(guard.TryAccept(long.MaxValue));
    }

    [Fact]
    public void Two_guards_do_not_share_state()
    {
        var first = new BridgeSequenceGuard();
        var second = new BridgeSequenceGuard();

        Assert.True(first.TryAccept(5));

        Assert.True(second.TryAccept(1));
        Assert.Equal(5, first.LastSeq);
    }
}
