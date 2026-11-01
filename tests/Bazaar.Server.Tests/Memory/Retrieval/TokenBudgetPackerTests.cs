using Bazaar.Server.Memory.Retrieval;

namespace Bazaar.Server.Tests.Memory.Retrieval;

public class TokenBudgetPackerTests
{
    private static FusedHit Hit(string id) => new(id, 0.0, FromLexical: true, FromVector: false, LexicalOnly: true);

    private static List<FusedHit> Hits(params string[] ids) => ids.Select(Hit).ToList();

    [Fact]
    public void Oversized_hit_is_skipped_and_later_smaller_hits_still_fit()
    {
        // budget 10: A(5) fits -> used 5; B(10) does not fit (15 > 10) -> skipped;
        // C(2) fits -> used 7; D(3) fits -> used 10. Total 10 is never exceeded.
        var tokens = new Dictionary<string, int> { ["A"] = 5, ["B"] = 10, ["C"] = 2, ["D"] = 3 };

        var packed = TokenBudgetPacker.Pack(Hits("A", "B", "C", "D"), tokens, tokenBudget: 10);

        Assert.Equal(["A", "C", "D"], packed.Select(h => h.Id).ToArray());
        Assert.True(packed.Sum(h => tokens[h.Id]) <= 10);
    }

    [Fact]
    public void Packing_stops_only_when_nothing_fits_and_never_exceeds_the_budget()
    {
        // budget 4: A(5) does not fit and is skipped; B(4) fits exactly -> used 4; nothing else is left.
        var tokens = new Dictionary<string, int> { ["A"] = 5, ["B"] = 4, ["C"] = 1 };

        var packed = TokenBudgetPacker.Pack(Hits("A", "B", "C"), tokens, tokenBudget: 4);

        Assert.Equal(["B"], packed.Select(h => h.Id).ToArray());
    }

    [Fact]
    public void Kept_hits_keep_their_rank_order()
    {
        var tokens = new Dictionary<string, int> { ["A"] = 1, ["B"] = 1, ["C"] = 1 };

        var packed = TokenBudgetPacker.Pack(Hits("C", "A", "B"), tokens, tokenBudget: 10);

        Assert.Equal(["C", "A", "B"], packed.Select(h => h.Id).ToArray());
    }

    [Fact]
    public void Empty_input_returns_empty()
    {
        var packed = TokenBudgetPacker.Pack(
            Array.Empty<FusedHit>(),
            new Dictionary<string, int>(),
            tokenBudget: 100);

        Assert.Empty(packed);
    }

    [Fact]
    public void Missing_token_count_is_rejected()
    {
        Assert.Throws<ArgumentException>(() =>
            TokenBudgetPacker.Pack(Hits("A"), new Dictionary<string, int>(), tokenBudget: 100));
    }

    [Fact]
    public void Negative_token_count_is_rejected()
    {
        var tokens = new Dictionary<string, int> { ["A"] = -1 };

        Assert.Throws<ArgumentException>(() =>
            TokenBudgetPacker.Pack(Hits("A"), tokens, tokenBudget: 100));
    }
}
