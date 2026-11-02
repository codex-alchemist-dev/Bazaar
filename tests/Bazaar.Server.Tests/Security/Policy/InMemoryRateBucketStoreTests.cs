using Bazaar.Server.Security.Policy;

namespace Bazaar.Server.Tests.Security.Policy;

public class InMemoryRateBucketStoreTests
{
    private static readonly RateRule _rule = new(2, TimeSpan.FromMinutes(1));

    private static readonly DateTimeOffset _start = PolicyFixtures._now;

    [Fact]
    public void Counts_reset_only_after_a_full_window()
    {
        var store = new InMemoryRateBucketStore();

        Assert.Equal(new RateCheck(true, 1), store.TryConsume("k", _rule, _start));
        Assert.Equal(new RateCheck(true, 2), store.TryConsume("k", _rule, _start.AddSeconds(10)));
        Assert.Equal(new RateCheck(false, 2), store.TryConsume("k", _rule, _start.AddSeconds(59)));

        // Exactly one window after the window start: the count resets.
        Assert.Equal(new RateCheck(true, 1), store.TryConsume("k", _rule, _start.AddMinutes(1)));
    }

    [Fact]
    public void A_refused_check_does_not_increment_the_count()
    {
        var store = new InMemoryRateBucketStore();
        store.TryConsume("k", _rule, _start);
        store.TryConsume("k", _rule, _start);

        var refused = store.TryConsume("k", _rule, _start.AddSeconds(30));
        var refusedAgain = store.TryConsume("k", _rule, _start.AddSeconds(45));

        Assert.Equal(new RateCheck(false, 2), refused);
        Assert.Equal(new RateCheck(false, 2), refusedAgain);
    }

    [Fact]
    public void A_refused_check_does_not_move_the_window_start()
    {
        var store = new InMemoryRateBucketStore();
        store.TryConsume("k", _rule, _start);
        store.TryConsume("k", _rule, _start.AddSeconds(5));
        store.TryConsume("k", _rule, _start.AddSeconds(30));

        // The window still began at _start, so the next window opens at _start + 1 minute, not at the refusal.
        Assert.Equal(new RateCheck(true, 1), store.TryConsume("k", _rule, _start.AddMinutes(1)));
    }

    [Fact]
    public void Keys_are_counted_independently()
    {
        var store = new InMemoryRateBucketStore();
        store.TryConsume("a", _rule, _start);
        store.TryConsume("a", _rule, _start);

        Assert.Equal(new RateCheck(true, 1), store.TryConsume("b", _rule, _start));
    }
}
