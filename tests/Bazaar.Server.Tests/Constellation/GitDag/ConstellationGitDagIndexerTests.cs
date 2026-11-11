using Bazaar.Server.Constellation.GitDag;

namespace Bazaar.Server.Tests.Constellation.GitDag;

public sealed class ConstellationGitDagIndexerTests
{
    private const string _repo = "/repos/vault";

    private static readonly DateTimeOffset _t0 = new(2026, 11, 1, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Linear_history_yields_one_node_per_commit_and_no_comet_edges()
    {
        var commits = new[]
        {
            new InMemoryGitDagProvider.FakeCommit("c1", [], _t0),
            new InMemoryGitDagProvider.FakeCommit("c2", ["c1"], _t0.AddMinutes(1)),
            new InMemoryGitDagProvider.FakeCommit("c3", ["c2"], _t0.AddMinutes(2)),
        };
        var indexer = new ConstellationGitDagIndexer(new InMemoryGitDagProvider(commits, head: "c3"));

        var index = await indexer.IndexAsync(_repo, "HEAD", CancellationToken.None);

        string[] expected = ["c1", "c2", "c3"];
        Assert.Equal(expected, index.Nodes.Select(n => n.Sha));
        Assert.Equal(2, index.Edges.Count);
        Assert.All(index.Edges, edge => Assert.False(edge.IsComet));
    }

    [Fact]
    public async Task One_merge_yields_exactly_one_comet_edge_from_the_merged_parent_to_the_target()
    {
        // main: root -> main1 -> merge
        // feature: root -> feature1, merged into main at merge (second parent is the merged tip)
        var commits = new[]
        {
            new InMemoryGitDagProvider.FakeCommit("root", [], _t0),
            new InMemoryGitDagProvider.FakeCommit("main1", ["root"], _t0.AddMinutes(1)),
            new InMemoryGitDagProvider.FakeCommit("feature1", ["root"], _t0.AddMinutes(2)),
            new InMemoryGitDagProvider.FakeCommit("merge", ["main1", "feature1"], _t0.AddMinutes(3)),
        };
        var indexer = new ConstellationGitDagIndexer(new InMemoryGitDagProvider(commits, head: "merge"));

        var index = await indexer.IndexAsync(_repo, "HEAD", CancellationToken.None);

        var comets = index.Edges.Where(e => e.IsComet).ToList();
        var comet = Assert.Single(comets);
        Assert.Equal("feature1", comet.FromSha);
        Assert.Equal("merge", comet.ToSha);
        Assert.Equal(4, index.Nodes.Count);
        string[] expectedParents = ["main1", "feature1"];
        Assert.Equal(expectedParents, index.Nodes.Single(n => n.Sha == "merge").ParentShas);
    }

    [Fact]
    public async Task Output_is_deterministic_across_two_runs_and_across_input_order()
    {
        var commits = new[]
        {
            new InMemoryGitDagProvider.FakeCommit("root", [], _t0),
            new InMemoryGitDagProvider.FakeCommit("main1", ["root"], _t0.AddMinutes(1)),
            new InMemoryGitDagProvider.FakeCommit("feature1", ["root"], _t0.AddMinutes(1)),
            new InMemoryGitDagProvider.FakeCommit("merge", ["main1", "feature1"], _t0.AddMinutes(2)),
        };
        var forward = new ConstellationGitDagIndexer(new InMemoryGitDagProvider(commits, head: "merge"));
        var reversed = new ConstellationGitDagIndexer(new InMemoryGitDagProvider(commits, head: "merge", reverseListOrder: true));

        var firstRun = Describe(await forward.IndexAsync(_repo, "HEAD", CancellationToken.None));
        var secondRun = Describe(await forward.IndexAsync(_repo, "HEAD", CancellationToken.None));
        var reversedRun = Describe(await reversed.IndexAsync(_repo, "HEAD", CancellationToken.None));

        Assert.Equal(firstRun, secondRun);
        Assert.Equal(firstRun, reversedRun);
    }

    [Fact]
    public async Task Nodes_with_the_same_time_are_ordered_by_sha()
    {
        var commits = new[]
        {
            new InMemoryGitDagProvider.FakeCommit("alpha", [], _t0),
            new InMemoryGitDagProvider.FakeCommit("zeta", ["alpha"], _t0),
        };
        var indexer = new ConstellationGitDagIndexer(new InMemoryGitDagProvider(commits, head: "zeta"));

        var index = await indexer.IndexAsync(_repo, "zeta", CancellationToken.None);

        string[] expected = ["alpha", "zeta"];
        Assert.Equal(expected, index.Nodes.Select(n => n.Sha));
    }

    [Fact]
    public async Task Empty_repository_yields_no_nodes()
    {
        var indexer = new ConstellationGitDagIndexer(new InMemoryGitDagProvider([], head: null));

        var index = await indexer.IndexAsync(_repo, null, CancellationToken.None);

        Assert.Empty(index.Nodes);
        Assert.Empty(index.Edges);
    }

    /// <summary>Flattens an index into comparable lines. Record equality would compare parent lists by reference.</summary>
    private static List<string> Describe(GitDagIndex index)
    {
        var lines = new List<string>();
        lines.AddRange(index.Nodes.Select(n => $"node {n.Sha} [{string.Join(",", n.ParentShas)}] {n.CommittedAt:O}"));
        lines.AddRange(index.Edges.Select(e => $"edge {e.FromSha}->{e.ToSha} comet={e.IsComet}"));
        return lines;
    }
}
