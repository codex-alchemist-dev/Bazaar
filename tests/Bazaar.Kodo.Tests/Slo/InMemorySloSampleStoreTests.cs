using Bazaar.Kodo.Slo;

namespace Bazaar.Kodo.Tests.Slo;

public sealed class InMemorySloSampleStoreTests
{
    [Fact]
    public void Samples_are_counted_per_slot_with_a_worst_wins_slot_verdict()
    {
        InMemorySloSampleStore store = new();
        SloTestKit.Put(store, "svc", 3, SloSampleOutcome.Good, 2);
        SloTestKit.Put(store, "svc", 2, SloSampleOutcome.Good);
        SloTestKit.Put(store, "svc", 2, SloSampleOutcome.Unobserved);
        SloTestKit.Put(store, "svc", 1, SloSampleOutcome.Unobserved);
        SloTestKit.Put(store, "svc", 1, SloSampleOutcome.Bad);

        long last = SloSlots.IndexOf(SloTestKit.Now, store.Resolution) - 1;
        SloSlotTotals totals = store.Read("svc", last - 5, last);

        Assert.Equal(new SloSlotTotals(3, 1, 2, 1, 1, 1, last - 2), totals);
        Assert.Equal(3, totals.OccupiedSlots);
    }

    [Fact]
    public void Bad_input_is_refused_and_changes_nothing()
    {
        InMemorySloSampleStore store = new();

        Assert.False(store.Record("Bad Id", SloTestKit.Now, SloSampleOutcome.Good));
        Assert.False(store.Record("", SloTestKit.Now, SloSampleOutcome.Good));
        Assert.False(store.Record("svc", SloTestKit.Now, (SloSampleOutcome)9));
        Assert.Equal(SloSlotTotals.Empty, store.Read("svc", 0, long.MaxValue));
        Assert.Equal(SloSlotTotals.Empty, store.Read("Bad Id", 0, long.MaxValue));
    }

    [Fact]
    public void Slots_are_bounded_and_the_oldest_is_dropped()
    {
        InMemorySloSampleStore store = new(maxSlots: 3);
        for (int m = 5; m >= 2; m--)
        {
            SloTestKit.Put(store, "svc", m, SloSampleOutcome.Good);
        }

        long last = SloSlots.IndexOf(SloTestKit.Now, store.Resolution) - 1;
        Assert.Equal(last - 3, store.Read("svc", 0, long.MaxValue).FirstSlot);
        Assert.Equal(3, store.Read("svc", 0, long.MaxValue).GoodSlots);

        // Older than everything kept while full: refused. An existing slot still takes samples.
        Assert.False(store.Record("svc", SloTestKit.Now - (10 * SloTestKit.Minute), SloSampleOutcome.Good));
        Assert.True(store.Record("svc", SloTestKit.Now - (4 * SloTestKit.Minute), SloSampleOutcome.Good));
    }

    [Fact]
    public void Series_are_bounded()
    {
        InMemorySloSampleStore store = new(maxSeries: 2);

        Assert.True(store.Record("a", SloTestKit.Now, SloSampleOutcome.Good));
        Assert.True(store.Record("b", SloTestKit.Now, SloSampleOutcome.Good));
        Assert.False(store.Record("c", SloTestKit.Now, SloSampleOutcome.Good));
        Assert.True(store.Record("a", SloTestKit.Now, SloSampleOutcome.Bad));
    }

    [Fact]
    public void Construction_limits_are_checked()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new InMemorySloSampleStore(TimeSpan.FromMilliseconds(500)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new InMemorySloSampleStore(TimeSpan.FromHours(2)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new InMemorySloSampleStore(maxSlots: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new InMemorySloSampleStore(maxSeries: 0));
    }
}
