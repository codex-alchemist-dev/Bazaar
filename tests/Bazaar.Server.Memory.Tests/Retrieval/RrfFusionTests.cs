using Bazaar.Server.Memory.Retrieval;

namespace Bazaar.Server.Tests.Memory.Retrieval;

public class RrfFusionTests
{
    private static RetrievalOptions Options(int topK = 10, int rrfK = 60) =>
        new(TokenBudget: 1000, TopK: topK, RrfK: rrfK);

    private static RankedHit[] Ranked(params string[] ids) =>
        ids.Select((id, i) => new RankedHit(id, ids.Length - i)).ToArray();

    [Fact]
    public void Two_lists_with_k_60_give_the_hand_computed_order_and_scores()
    {
        // lexical: A rank 1, B rank 2, C rank 3
        // vector:  B rank 1, D rank 2, A rank 3
        //
        // A = 1/(60+1) + 1/(60+3) = 1/61 + 1/63 = 0.0322664755
        // B = 1/(60+2) + 1/(60+1) = 1/62 + 1/61 = 0.0325224749
        // C = 1/(60+3)            = 1/63        = 0.0158730159
        // D = 1/(60+2)            = 1/62        = 0.0161290323
        //
        // Expected order: B, A, D, C.
        var result = RrfFusion.Fuse(
            Ranked("A", "B", "C"),
            Ranked("B", "D", "A"),
            Options());

        Assert.Equal(["B", "A", "D", "C"], result.Select(h => h.Id).ToArray());
        Assert.Equal(1.0 / 62 + 1.0 / 61, result[0].Score, 12);
        Assert.Equal(1.0 / 61 + 1.0 / 63, result[1].Score, 12);
        Assert.Equal(1.0 / 62, result[2].Score, 12);
        Assert.Equal(1.0 / 63, result[3].Score, 12);
    }

    [Fact]
    public void Id_in_both_lists_ranks_above_id_in_one_list_at_the_same_best_rank()
    {
        // lexical: a rank 1, x rank 2
        // vector:  b rank 1, a rank 2
        // a = 1/61 + 1/62 ; b = 1/61 (vector rank 1 only). b and a both have rank 1 somewhere,
        // but a is in both lists, so a scores higher.
        var result = RrfFusion.Fuse(
            Ranked("a", "x"),
            Ranked("b", "a"),
            Options());

        Assert.Equal("a", result[0].Id);
        Assert.True(result[0].FromLexical);
        Assert.True(result[0].FromVector);
        Assert.Equal("b", result[1].Id);
        Assert.False(result[1].FromLexical);
        Assert.True(result[1].FromVector);
    }

    [Fact]
    public void Equal_scores_break_ties_by_id_ascending()
    {
        // lexical: b rank 1 -> 1/61 ; vector: a rank 1 -> 1/61. Equal scores, so id order decides.
        var result = RrfFusion.Fuse(
            Ranked("b"),
            Ranked("a"),
            Options());

        Assert.Equal(1.0 / 61, result[0].Score, 12);
        Assert.Equal(1.0 / 61, result[1].Score, 12);
        Assert.Equal(["a", "b"], result.Select(h => h.Id).ToArray());
    }

    [Fact]
    public void Top_k_keeps_only_the_best_hits()
    {
        var result = RrfFusion.Fuse(
            Ranked("A", "B", "C"),
            Ranked("B", "D", "A"),
            Options(topK: 2));

        Assert.Equal(["B", "A"], result.Select(h => h.Id).ToArray());
    }

    [Fact]
    public void Duplicate_id_inside_one_list_counts_only_at_its_first_rank()
    {
        // lexical: A rank 1, A again at rank 2 (ignored), B rank 3.
        // A = 1/61 only; B = 1/63.
        var result = RrfFusion.Fuse(
            Ranked("A", "A", "B"),
            Array.Empty<RankedHit>(),
            Options());

        Assert.Equal(["A", "B"], result.Select(h => h.Id).ToArray());
        Assert.Equal(1.0 / 61, result[0].Score, 12);
        Assert.Equal(1.0 / 63, result[1].Score, 12);
    }

    [Fact]
    public void Lexical_only_flag_is_true_when_the_vector_list_is_empty()
    {
        var result = RrfFusion.Fuse(
            Ranked("A", "B"),
            Array.Empty<RankedHit>(),
            Options());

        Assert.All(result, hit => Assert.True(hit.LexicalOnly));
        Assert.All(result, hit => Assert.True(hit.FromLexical));
        Assert.All(result, hit => Assert.False(hit.FromVector));
    }

    [Fact]
    public void Lexical_only_flag_is_false_when_the_vector_list_has_hits()
    {
        var result = RrfFusion.Fuse(
            Ranked("A"),
            Ranked("B"),
            Options());

        Assert.All(result, hit => Assert.False(hit.LexicalOnly));
    }

    [Fact]
    public void Empty_inputs_return_empty()
    {
        var result = RrfFusion.Fuse(
            Array.Empty<RankedHit>(),
            Array.Empty<RankedHit>(),
            Options());

        Assert.Empty(result);
    }

    [Fact]
    public void Rrf_constant_below_one_is_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            RrfFusion.Fuse(Ranked("A"), Ranked("A"), Options(rrfK: 0)));
    }
}
