using Bazaar.Server.Vault.Search;

namespace Bazaar.Server.Tests.Vault.Search;

/// <summary>Pure search rules: RRF with fixed ranks, the access rule, and card text. No database.</summary>
public sealed class VaultSearchUnitTests
{
    [Fact]
    public void Rrf_scores_each_list_by_rank_and_sums_keys_that_appear_in_both()
    {
        var fused = ReciprocalRankFusion.Fuse<string>([["a", "b", "c"], ["b", "d"]], k: 60);

        // b is rank 2 in the first list and rank 1 in the second; a is rank 1 in one list only.
        Assert.Equal(["b", "a", "d", "c"], fused.Select(entry => entry.Key));
        Assert.Equal(1.0 / 62 + 1.0 / 61, fused[0].Score, precision: 12);
        Assert.Equal(1.0 / 61, fused[1].Score, precision: 12);
        Assert.Equal(1.0 / 63, fused[3].Score, precision: 12);
    }

    [Fact]
    public void Rrf_breaks_ties_by_first_seen_order()
    {
        var fused = ReciprocalRankFusion.Fuse<string>([["x"], ["y"]], k: 10);

        Assert.Equal(["x", "y"], fused.Select(entry => entry.Key));
        Assert.Equal(fused[0].Score, fused[1].Score);
    }

    [Fact]
    public void Rrf_rejects_a_non_positive_constant()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ReciprocalRankFusion.Fuse<string>([["a"]], k: 0));
    }

    [Fact]
    public void Shared_documents_are_readable_by_any_caller_and_private_ones_only_by_their_agent_or_the_owner()
    {
        var alpha = new VaultCaller(IsOwner: false, AgentOwner: "agent:alpha");
        var beta = new VaultCaller(IsOwner: false, AgentOwner: "agent:beta");

        Assert.True(VaultAccess.CanRead(documentOwner: null, beta));
        Assert.True(VaultAccess.CanRead("agent:alpha", alpha));
        Assert.False(VaultAccess.CanRead("agent:alpha", beta));
        Assert.False(VaultAccess.CanRead("agent:alpha", new VaultCaller(IsOwner: false, AgentOwner: null)));
        Assert.True(VaultAccess.CanRead("agent:alpha", VaultCaller.Owner));
    }

    [Fact]
    public void Gist_collapses_whitespace_and_truncates_with_an_ellipsis()
    {
        Assert.Equal("one two three", VaultSearchService.Gist("one\n\n  two\tthree", 200));
        Assert.Equal("one tw...", VaultSearchService.Gist("one two three four", 9));
    }

    [Fact]
    public void Approximate_tokens_round_up_at_four_characters_per_token()
    {
        Assert.Equal(0, VaultSearchService.ApproxTokens(""));
        Assert.Equal(1, VaultSearchService.ApproxTokens("abcd"));
        Assert.Equal(2, VaultSearchService.ApproxTokens("abcde"));
    }
}
