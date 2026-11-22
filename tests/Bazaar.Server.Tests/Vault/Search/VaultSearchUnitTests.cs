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

    private static readonly VaultCaller _alpha = new(IsOwner: false, AgentOwner: "agent:alpha");
    private static readonly VaultCaller _beta = new(IsOwner: false, AgentOwner: "agent:beta");
    private static readonly VaultCaller _anonymous = new(IsOwner: false, AgentOwner: null);

    [Fact]
    public void Private_documents_are_readable_only_by_their_agent_or_the_owner()
    {
        const string path = "agents/alpha/memory/plan.md";
        Assert.True(VaultAccess.CanRead(path, "agent:alpha", _alpha));
        Assert.False(VaultAccess.CanRead(path, "agent:alpha", _beta));
        Assert.False(VaultAccess.CanRead(path, "agent:alpha", _anonymous));
        Assert.True(VaultAccess.CanRead(path, "agent:alpha", VaultCaller.Owner));
    }

    [Theory]
    [InlineData("company/Constitution.md")]
    [InlineData("company/memory/policy.md")]
    [InlineData("shared/research/auth.md")]
    public void Ownerless_documents_in_company_and_shared_areas_are_readable_by_every_agent(string path)
    {
        Assert.True(VaultAccess.CanRead(path, documentOwner: null, _alpha));
        Assert.True(VaultAccess.CanRead(path, documentOwner: null, _beta));
        Assert.True(VaultAccess.CanRead(path, documentOwner: null, VaultCaller.Owner));
    }

    // Regression: an ownerless document used to be readable by every caller, so Vault search ignored project membership.
    [Theory]
    [InlineData("projects/x/Plan.md")]
    [InlineData("projects/x/deep-dives/auth.md")]
    [InlineData("hive/procedure/retry-backoff.md")]
    [InlineData("decisions/0001-use-postgres.md")]
    [InlineData("drafts/alpha/idea.md")]
    [InlineData("twin/x/overview.md")]
    [InlineData("Index.md")]
    [InlineData("agents/alpha/charter.md")]
    [InlineData("companyish/notes.md")]
    public void Ownerless_documents_outside_an_agent_readable_area_are_the_owners_only(string path)
    {
        Assert.False(VaultAccess.CanRead(path, documentOwner: null, _beta));
        Assert.False(VaultAccess.CanRead(path, documentOwner: null, _anonymous));
        Assert.True(VaultAccess.CanRead(path, documentOwner: null, VaultCaller.Owner));
    }

    [Fact]
    public void An_agent_reads_ownerless_files_in_its_own_area_only()
    {
        Assert.True(VaultAccess.CanRead("agents/alpha/charter.md", documentOwner: null, _alpha));
        Assert.False(VaultAccess.CanRead("agents/alpha/charter.md", documentOwner: null, _beta));
        Assert.False(VaultAccess.CanRead("agents/alphabet/charter.md", documentOwner: null, _alpha));
    }

    [Fact]
    public void A_caller_that_is_neither_owner_nor_agent_reads_nothing()
    {
        Assert.False(VaultAccess.CanRead("company/Constitution.md", documentOwner: null, _anonymous));
        Assert.False(VaultAccess.CanRead("shared/research/auth.md", documentOwner: null, _anonymous));
    }

    [Fact]
    public void An_owner_field_overrides_an_agent_readable_area()
    {
        Assert.False(VaultAccess.CanRead("shared/memory/alpha.md", "agent:alpha", _beta));
        Assert.True(VaultAccess.CanRead("shared/memory/alpha.md", "agent:alpha", _alpha));
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
