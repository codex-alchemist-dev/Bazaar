using Bazaar.Server.Council.Prompts;

namespace Bazaar.Server.Tests.Council.Prompts;

public class ReasonOrderTests
{
    [Fact]
    public void Same_seed_and_count_give_the_same_order()
    {
        var first = ReasonOrder.Permutation(12, 42L);
        var second = ReasonOrder.Permutation(12, 42L);

        Assert.Equal(first, second);
    }

    [Fact]
    public void Different_seeds_usually_give_different_orders()
    {
        var baseline = ReasonOrder.Permutation(10, 1L);

        var differing = Enumerable.Range(2, 20)
            .Count(seed => !ReasonOrder.Permutation(10, seed).SequenceEqual(baseline));

        Assert.True(differing > 15, $"Only {differing} of 20 seeds changed the order.");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(7)]
    [InlineData(50)]
    public void Permutation_contains_every_index_once(int count)
    {
        var order = ReasonOrder.Permutation(count, -9_000_000_001L);

        Assert.Equal(Enumerable.Range(0, count), order.OrderBy(index => index));
    }

    [Fact]
    public void Negative_count_is_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ReasonOrder.Permutation(-1, 1L));
    }

    [Fact]
    public void Shuffle_returns_items_in_the_permuted_order_and_leaves_input_alone()
    {
        IReadOnlyList<string> items = ["a", "b", "c", "d", "e"];
        var permutation = ReasonOrder.Permutation(items.Count, 7L);

        var shuffled = ReasonOrder.Shuffle(items, 7L);

        Assert.Equal(permutation.Select(index => items[index]), shuffled);
        Assert.Equal(["a", "b", "c", "d", "e"], items);
    }

    [Fact]
    public void Aliases_are_letters_by_position_and_stable()
    {
        Assert.Equal("Reason A", ReasonOrder.AliasAt(0));
        Assert.Equal("Reason Z", ReasonOrder.AliasAt(25));
        Assert.Equal("Reason AA", ReasonOrder.AliasAt(26));
        Assert.Equal("Reason AB", ReasonOrder.AliasAt(27));
        Assert.Equal("Reason AZ", ReasonOrder.AliasAt(51));
        Assert.Equal("Reason BA", ReasonOrder.AliasAt(52));
        Assert.Equal(ReasonOrder.AliasAt(3), ReasonOrder.AliasAt(3));
    }

    [Fact]
    public void Assign_aliases_labels_reasons_in_the_given_order()
    {
        IReadOnlyList<string> reasons = ["First reason.", "Second reason.", "Third reason."];

        var revealed = ReasonOrder.AssignAliases(reasons);

        Assert.Equal(
            [
                new RevealedReason("Reason A", "First reason."),
                new RevealedReason("Reason B", "Second reason."),
                new RevealedReason("Reason C", "Third reason."),
            ],
            revealed);
    }

    [Fact]
    public void Aliases_for_the_same_seed_are_stable_across_calls()
    {
        IReadOnlyList<string> reasons = ["one.", "two.", "three.", "four."];

        var first = ReasonOrder.AssignAliases(ReasonOrder.Shuffle(reasons, 99L));
        var second = ReasonOrder.AssignAliases(ReasonOrder.Shuffle(reasons, 99L));

        Assert.Equal(first, second);
    }
}
