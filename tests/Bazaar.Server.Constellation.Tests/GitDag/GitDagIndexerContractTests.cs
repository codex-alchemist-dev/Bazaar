using Bazaar.Server.Constellation.GitDag;

namespace Bazaar.Server.Tests.Constellation.GitDag;

public sealed class GitDagIndexerContractTests
{
    private static readonly DateTimeOffset _t0 = new(2026, 11, 1, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Indexer_is_reachable_through_IGitDagIndexer_and_gives_the_same_index()
    {
        var commits = new[]
        {
            new InMemoryGitDagProvider.FakeCommit("c1", [], _t0),
            new InMemoryGitDagProvider.FakeCommit("c2", ["c1"], _t0.AddMinutes(1)),
        };
        var concrete = new ConstellationGitDagIndexer(new InMemoryGitDagProvider(commits, head: "c2"));

        var viaContract = await AsContract(concrete).IndexAsync("/repos/vault", "HEAD", CancellationToken.None);
        var direct = await concrete.IndexAsync("/repos/vault", "HEAD", CancellationToken.None);

        // GitDagIndex holds lists, so record equality is by reference; compare the contents instead.
        Assert.Equal(Describe(direct), Describe(viaContract));
    }

    [Fact]
    public async Task Blank_tip_through_the_contract_gives_the_empty_index()
    {
        var index = await AsContract(new ConstellationGitDagIndexer(new InMemoryGitDagProvider([])))
            .IndexAsync("/repos/vault", null, CancellationToken.None);

        Assert.Same(GitDagIndex.Empty, index);
    }

    /// <summary>Views the indexer through the contract only, so the tests exercise the interface a server module would use.</summary>
    private static IGitDagIndexer AsContract(IGitDagIndexer indexer) => indexer;

    private static string Describe(GitDagIndex index) =>
        string.Join(
            "|",
            index.Nodes.Select(n => $"{n.Sha}:{string.Join(",", n.ParentShas)}@{n.CommittedAt:O}")
                .Concat(index.Edges.Select(e => $"{e.FromSha}->{e.ToSha}:{e.IsComet}")));
}
