using Bazaar.Testing;

namespace Bazaar.Server.Tests.Testing;

public class DeterministicIdsTests
{
    [Fact]
    public void Same_seed_gives_the_same_guid_sequence()
    {
        var first = new DeterministicIds(new TestSeed(42));
        var second = new DeterministicIds(new TestSeed(42));

        var a = Enumerable.Range(0, 50).Select(_ => first.NextGuid()).ToArray();
        var b = Enumerable.Range(0, 50).Select(_ => second.NextGuid()).ToArray();

        Assert.Equal(a, b);
    }

    [Fact]
    public void Different_seeds_give_different_sequences()
    {
        var a = new DeterministicIds(new TestSeed(1)).NextGuid();
        var b = new DeterministicIds(new TestSeed(2)).NextGuid();

        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Output_is_pinned_for_a_known_seed()
    {
        // Pins the algorithm: a change here would silently change every seeded replay.
        var ids = new DeterministicIds(new TestSeed(0));

        Assert.Equal(0xE220A8397B1DCDAFUL, ids.NextUInt64());
        Assert.Equal(0x6E789E6AA1B965F4UL, ids.NextUInt64());
    }

    [Fact]
    public void Guids_do_not_repeat_within_a_large_sample()
    {
        var ids = new DeterministicIds(new TestSeed(7));

        var distinct = Enumerable.Range(0, 10_000).Select(_ => ids.NextGuid()).Distinct().Count();

        Assert.Equal(10_000, distinct);
    }

    [Fact]
    public void NextId_is_prefix_underscore_and_32_lowercase_hex()
    {
        var ids = new DeterministicIds(new TestSeed(3));

        var id = ids.NextId("agent");

        Assert.Matches("^agent_[0-9a-f]{32}$", id);
    }

    [Fact]
    public void NextId_rejects_an_empty_prefix()
    {
        var ids = new DeterministicIds(new TestSeed(3));

        Assert.Throws<ArgumentException>(() => ids.NextId(""));
    }

    [Fact]
    public void NextInt_stays_in_range_and_is_reproducible()
    {
        var first = new DeterministicIds(new TestSeed(9));
        var second = new DeterministicIds(new TestSeed(9));

        for (var i = 0; i < 1_000; i++)
        {
            var value = first.NextInt(7);
            Assert.InRange(value, 0, 6);
            Assert.Equal(value, second.NextInt(7));
        }
    }

    [Fact]
    public void NextInt_rejects_a_non_positive_bound()
    {
        var ids = new DeterministicIds(new TestSeed(9));

        Assert.Throws<ArgumentOutOfRangeException>(() => ids.NextInt(0));
    }

    [Fact]
    public async Task Concurrent_callers_draw_distinct_values_from_one_sequence()
    {
        var ids = new DeterministicIds(new TestSeed(11));

        var tasks = Enumerable.Range(0, 8)
            .Select(_ => Task.Run(() => Enumerable.Range(0, 500).Select(_ => ids.NextUInt64()).ToArray()))
            .ToArray();
        var all = (await Task.WhenAll(tasks)).SelectMany(values => values).ToArray();

        Assert.Equal(all.Length, all.Distinct().Count());
    }
}
