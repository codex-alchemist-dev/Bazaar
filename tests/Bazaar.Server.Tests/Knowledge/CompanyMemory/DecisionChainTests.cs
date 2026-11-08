using Bazaar.Server.Knowledge.CompanyMemory;

namespace Bazaar.Server.Tests.Knowledge.CompanyMemory;

public sealed class DecisionChainTests
{
    private const string _a = "decisions/0001-use-postgres.md";
    private const string _b = "decisions/0002-use-notify.md";
    private const string _c = "decisions/0003-use-valkey.md";

    private static DecisionRecord Record(string path, string status = "accepted", string[]? supersedes = null, string? supersededBy = null) =>
        new(path, status, supersedes ?? [], supersededBy, DecidedIn: null);

    [Fact]
    public void Supersedes_entry_produces_an_edge_from_the_newer_record()
    {
        var links = DecisionChain.Derive([
            Record(_a, "superseded"),
            Record(_b, "accepted", supersedes: ["0001-use-postgres"]),
        ]);

        var link = Assert.Single(links);
        Assert.Equal(_b, link.FromDoc);
        Assert.Equal(_a, link.ToDoc);
        Assert.Equal("supersedes", link.Relation);
        Assert.Equal("accepted", link.Status);
        Assert.False(link.FromMissing);
        Assert.False(link.ToMissing);
    }

    [Fact]
    public void Superseded_by_entry_produces_the_same_edge_in_the_same_direction()
    {
        var links = DecisionChain.Derive([
            Record(_a, "superseded", supersededBy: "0002-use-notify"),
            Record(_b),
        ]);

        var link = Assert.Single(links);
        Assert.Equal(_b, link.FromDoc);
        Assert.Equal(_a, link.ToDoc);
        Assert.Equal("accepted", link.Status);
    }

    [Fact]
    public void Both_directions_declaring_the_same_fact_yield_one_edge()
    {
        var links = DecisionChain.Derive([
            Record(_a, "superseded", supersededBy: "0002-use-notify"),
            Record(_b, "accepted", supersedes: ["decisions/0001-use-postgres.md"]),
        ]);

        var link = Assert.Single(links);
        Assert.Equal(_b, link.FromDoc);
        Assert.Equal(_a, link.ToDoc);
    }

    [Fact]
    public void Unresolved_supersedes_target_is_kept_and_flagged_as_missing_target()
    {
        var links = DecisionChain.Derive([
            Record(_b, "accepted", supersedes: ["0009-gone"]),
        ]);

        var link = Assert.Single(links);
        Assert.Equal(_b, link.FromDoc);
        Assert.Equal("decisions/0009-gone.md", link.ToDoc);
        Assert.False(link.FromMissing);
        Assert.True(link.ToMissing);
    }

    [Fact]
    public void Unresolved_superseded_by_source_is_kept_with_null_status_and_missing_source()
    {
        var links = DecisionChain.Derive([
            Record(_a, "superseded", supersededBy: "0008-gone"),
        ]);

        var link = Assert.Single(links);
        Assert.Equal("decisions/0008-gone.md", link.FromDoc);
        Assert.Equal(_a, link.ToDoc);
        Assert.Null(link.Status);
        Assert.True(link.FromMissing);
        Assert.False(link.ToMissing);
    }

    [Fact]
    public void Empty_input_gives_no_edges()
    {
        Assert.Empty(DecisionChain.Derive([]));
    }

    [Fact]
    public void Records_without_chain_keys_give_no_edges()
    {
        Assert.Empty(DecisionChain.Derive([Record(_a), Record(_b)]));
    }

    [Fact]
    public void Output_is_sorted_and_does_not_depend_on_input_order()
    {
        var first = Record(_a, "superseded", supersededBy: "0002-use-notify");
        var second = Record(_b, "superseded", supersedes: ["0001-use-postgres"], supersededBy: "0003-use-valkey");
        var third = Record(_c, "accepted", supersedes: ["0002-use-notify", "0009-gone"]);

        var forward = DecisionChain.Derive([first, second, third]);
        var reverse = DecisionChain.Derive([third, second, first]);

        Assert.Equal(forward, reverse);
        var ordered = forward.Select(link => (link.FromDoc, link.ToDoc)).ToList();
        Assert.Equal(ordered.OrderBy(pair => pair.FromDoc, StringComparer.Ordinal).ThenBy(pair => pair.ToDoc, StringComparer.Ordinal), ordered);
    }

    [Fact]
    public void Duplicate_references_in_one_record_collapse_to_one_edge()
    {
        var links = DecisionChain.Derive([
            Record(_a, "superseded"),
            Record(_b, "accepted", supersedes: ["0001-use-postgres", "decisions/0001-use-postgres.md"]),
        ]);

        Assert.Single(links);
    }

    [Fact]
    public void Two_records_with_the_same_path_are_refused()
    {
        Assert.Throws<ArgumentException>(() => DecisionChain.Derive([Record(_a), Record(_a)]));
    }

    [Fact]
    public void Blank_reference_is_refused()
    {
        Assert.Throws<ArgumentException>(() => DecisionChain.Derive([Record(_b, supersedes: ["  "])]));
    }
}
